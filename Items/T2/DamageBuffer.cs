﻿using RoR2;
using UnityEngine;
using System.Collections.ObjectModel;
using TILER2;
using R2API;
using static TILER2.MiscUtil;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkInvisible.TinkersSatchel {
    public class DamageBuffer : Item<DamageBuffer> {

        ////// Item Data //////

        public override string displayName => "Negative Feedback Loop";
        public override ItemTier itemTier => ItemTier.Tier2;
        public override ReadOnlyCollection<ItemTag> itemTags => new ReadOnlyCollection<ItemTag>(new[] { ItemTag.Healing });

        protected override string GetNameString(string langid = null) => displayName;
        protected override string GetPickupString(string langid = null) => "Some incoming damage is dealt over time.";
        protected override string GetDescString(string langid = null) => $"<style=cIsDamage>{Pct(bufferFrac)} <style=cStack>(+{Pct(bufferFrac)} per stack, inverse-mult.)</style> of incoming damage</style> is <style=cIsHealing>applied gradually</style> over {bufferDuration} seconds, ticking every {bufferRate} seconds. <style=cIsHealing>Healing</style> past <style=cIsHealth>max health</style> <style=cIsHealing>will apply</style> to the pool of delayed damage.";
        protected override string GetLoreString(string langid = null) => "";



        ////// Config ///////

        [AutoConfigUpdateActions(AutoConfigUpdateActionTypes.InvalidateLanguage)]
        [AutoConfig("Amount of damage to absorb per stack (inverse-mult.).", AutoConfigFlags.PreventNetMismatch, 0f, 1f)]
        public float bufferFrac { get; private set; } = 0.2f;

        [AutoConfigUpdateActions(AutoConfigUpdateActionTypes.InvalidateLanguage)]
        [AutoConfig("Time over which each damage instance is delayed, in seconds.", AutoConfigFlags.PreventNetMismatch, 0f, float.MaxValue)]
        public float bufferDuration { get; private set; } = 5f;

        [AutoConfigUpdateActions(AutoConfigUpdateActionTypes.InvalidateLanguage)]
        [AutoConfig("Tick interval of the damage buffer, in seconds.", AutoConfigFlags.PreventNetMismatch, 0f, float.MaxValue)]
        public float bufferRate { get; private set; } = 0.2f;



        ////// TILER2 Module Setup //////
        
        public DamageBuffer() {
            modelResource = TinkersSatchelPlugin.resources.LoadAsset<GameObject>("Assets/TinkersSatchel/Prefabs/DamageBuffer.prefab");
            iconResource = TinkersSatchelPlugin.resources.LoadAsset<Sprite>("Assets/TinkersSatchel/Textures/Icons/damageBufferIcon.png");
        }

        public override void SetupAttributes() {
            base.SetupAttributes();
        }

        public override void Install() {
            base.Install();
            IL.RoR2.HealthComponent.TakeDamage += HealthComponent_TakeDamage;
            IL.RoR2.HealthComponent.Heal += HealthComponent_Heal;
        }

        public override void Uninstall() {
            base.Uninstall();
            IL.RoR2.HealthComponent.TakeDamage -= HealthComponent_TakeDamage;
            IL.RoR2.HealthComponent.Heal -= HealthComponent_Heal;
        }



        ////// Hooks //////

        private void HealthComponent_TakeDamage(ILContext il) {
            ILCursor c = new ILCursor(il);

            int locIndex = 0;
            if(c.TryGotoNext(
                x => x.MatchLdloc(out locIndex),
                x => x.MatchLdcR4(0),
                x => x.MatchBleUn(out _),
                x => x.MatchLdarg(0),
                x => x.MatchLdfld<HealthComponent>(nameof(HealthComponent.barrier))
                )
                && c.TryGotoPrev(MoveType.After,
                x => x.MatchStloc((byte)locIndex))
                ) {
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldloc_S, (byte)locIndex);
                c.EmitDelegate<Func<HealthComponent, float, float>>((hc, origFinalDamage) => {
                    var count = GetCount(hc?.body);
                    if(count <= 0) return origFinalDamage;
                    var cpt = hc.GetComponent<DelayedDamageBufferComponent>();
                    if(!cpt) cpt = hc.gameObject.AddComponent<DelayedDamageBufferComponent>();
                    if(cpt.isApplying) return origFinalDamage;
                    var frac = Mathf.Clamp01(1f-1f/(1f + bufferFrac * (float)count));
                    var reduc = origFinalDamage * frac;
                    cpt.ApplyDamage(reduc);
                    return origFinalDamage - reduc;
                });
                c.Emit(OpCodes.Stloc_S, (byte)locIndex);
            } else {
                TinkersSatchelPlugin._logger.LogError("Failed to apply IL patch (target instructions not found): DamageBuffer::HealthComponent_TakeDamage");
            }
        }

        private void HealthComponent_Heal(ILContext il) {
            ILCursor c = new ILCursor(il);

            int locIndex = 0;
            if(c.TryGotoNext(MoveType.Before,
                x => x.MatchLdloc(out locIndex),
                x => x.MatchLdcR4(0),
                x => x.MatchBleUn(out _),
                x => x.MatchLdarg(out _),
                x => x.MatchBrfalse(out _),
                x => x.MatchLdarg(out _),
                x => x.MatchLdflda<HealthComponent>(nameof(HealthComponent.itemCounts)),
                x => x.MatchLdfld<HealthComponent.ItemCounts>(nameof(HealthComponent.ItemCounts.barrierOnOverHeal))
                )) {
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldloc, locIndex);
                c.EmitDelegate<Action<HealthComponent, float>>((hc, overheal) => {
                    if(!hc) return;
                    var cpt = hc.GetComponent<DelayedDamageBufferComponent>();
                    if(cpt)
                        cpt.ApplyOverheal(overheal);
                });
            } else {
                TinkersSatchelPlugin._logger.LogError("Failed to apply IL patch (target instructions not found): DamageBuffer::HealthComponent_Heal");
            }
        }
    }

    [RequireComponent(typeof(HealthComponent))]
    public class DelayedDamageBufferComponent : MonoBehaviour {
        HealthComponent hc;
        public List<(float curr, float max)> bufferDamage = new List<(float, float)>();
        float stopwatch = 0f;
        public bool isApplying { get; private set; } = false;

        void Awake() {
            hc = GetComponent<HealthComponent>();
        }

        void FixedUpdate() {
            if(bufferDamage.Count > 0) {
                stopwatch -= Time.fixedDeltaTime;
                if(stopwatch <= 0f) {
                    stopwatch = DamageBuffer.instance.bufferRate;
                    float accum = 0f;
                    var frac = DamageBuffer.instance.bufferRate / DamageBuffer.instance.bufferDuration;
                    for(var i = 0; i < bufferDamage.Count; i++) {
                        var rem = Mathf.Min(bufferDamage[i].max * frac, bufferDamage[i].curr);
                        accum += rem;
                        bufferDamage[i] = (bufferDamage[i].curr - rem, bufferDamage[i].max);
                    }
                    bufferDamage.RemoveAll(x => x.curr <= 0f);
                    isApplying = true;
                    hc.TakeDamage(new DamageInfo {
                        attacker = null,
                        crit = false,
                        damage = accum,
                        force = Vector3.zero,
                        inflictor = null,
                        position = hc.body?.corePosition ?? transform.position,
                        procCoefficient = 0,
                        damageColorIndex = DamageColorIndex.Item,
                        damageType = DamageType.BypassArmor | DamageType.BypassBlock | DamageType.DoT
                    });
                    isApplying = false;
                }
            }
        }

        public void ApplyDamage(float amount) {
            if(amount > 0f)
                bufferDamage.Add((amount, amount));
        }

        public void ApplyOverheal(float amount) {
            if(bufferDamage.Count == 0 || amount <= 0f) return;
            var total = bufferDamage.Sum(x => x.curr);
            var frac = amount / total;
            for(var i = 0; i < bufferDamage.Count; i++) {
                var reduc = bufferDamage[i].curr * frac;
                var remaining = bufferDamage[i].curr - reduc;
                bufferDamage[i] = (remaining, Mathf.Max(bufferDamage[i].max - reduc, remaining));
            }
            bufferDamage.RemoveAll(x => x.curr <= 0f);
        }
    }
}