﻿using RoR2;
using UnityEngine;
using System.Collections.ObjectModel;
using TILER2;
using R2API;
using static TILER2.MiscUtil;

namespace ThinkInvisible.TinkersSatchel {
	public class Skein : Item<Skein> {

		////// Item Data //////

		public override string displayName => "Spacetime Skein";
		public override ItemTier itemTier => ItemTier.Tier3;
		public override ReadOnlyCollection<ItemTag> itemTags => new ReadOnlyCollection<ItemTag>(new[] { ItemTag.Utility, ItemTag.Damage });

		protected override string GetNameString(string langid = null) => displayName;
		protected override string GetPickupString(string langid = null) =>
			"Gain mass while stationary. Lose mass while moving.";
		protected override string GetDescString(string langid = null) =>
			$"Standing still reduces the next damage and knockback you take by up to {Pct(highMassFrac)} (+{Pct(highMassFrac)} per stack, hyperbolic). Moving increasing your move and attack speed by up to {Pct(lowMassFrac)} (+{Pct(lowMassFrac)} per stack, linear). Effect ramps up over {massChangeDuration:N0} seconds, and is lost once you start or stop moving (latter has a brief grace period).";
		protected override string GetLoreString(string langid = null) => "";



		////// Config //////

		[AutoConfig("Maximum damage/knockback to block per stack (hyperbolic).", AutoConfigFlags.PreventNetMismatch, 0f, 1f)]
		public float highMassFrac { get; private set; } = 0.5f;

		[AutoConfig("Maximum speed to add per stack (linear).", AutoConfigFlags.PreventNetMismatch, 0f, 1f)]
		public float lowMassFrac { get; private set; } = 0.5f;

		[AutoConfig("Time required to reach maximum buff, in seconds.", AutoConfigFlags.PreventNetMismatch, 0f, float.MaxValue)]
		public float massChangeDuration { get; private set; } = 5f;

		[AutoConfig("Time required to register a movement stop, in seconds.", AutoConfigFlags.PreventNetMismatch, 0f, float.MaxValue)]
		public float moveGracePeriod { get; private set; } = 0.25f;



		////// TILER2 Module Setup //////

		public Skein() {
			modelResource = TinkersSatchelPlugin.resources.LoadAsset<GameObject>("Assets/TinkersSatchel/Prefabs/Skein.prefab");
			iconResource = TinkersSatchelPlugin.resources.LoadAsset<Sprite>("Assets/TinkersSatchel/Textures/Icons/skeinIcon.png");
		}

		public override void SetupAttributes() {
			base.SetupAttributes();
		}

		public override void Install() {
			base.Install();
			CharacterBody.onBodyInventoryChangedGlobal += CharacterBody_onBodyInventoryChangedGlobal;
			RecalculateStatsAPI.GetStatCoefficients += RecalculateStatsAPI_GetStatCoefficients;
            On.RoR2.HealthComponent.TakeDamage += HealthComponent_TakeDamage;
		}

        public override void Uninstall() {
			base.Uninstall();
			CharacterBody.onBodyInventoryChangedGlobal -= CharacterBody_onBodyInventoryChangedGlobal;
			RecalculateStatsAPI.GetStatCoefficients -= RecalculateStatsAPI_GetStatCoefficients;
			On.RoR2.HealthComponent.TakeDamage -= HealthComponent_TakeDamage;
		}



		////// Hooks //////
		
		private void CharacterBody_onBodyInventoryChangedGlobal(CharacterBody body) {
			if(GetCount(body) > 0 && !body.GetComponent<SkeinTracker>())
				body.gameObject.AddComponent<SkeinTracker>();
		}

		private void RecalculateStatsAPI_GetStatCoefficients(CharacterBody sender, RecalculateStatsAPI.StatHookEventArgs args) {
			if(!sender) return;
			var count = GetCount(sender);
			var cpt = sender.GetComponent<SkeinTracker>();
			if(count > 0 && cpt) {
				var fac = cpt.GetMovementScalar() * count * lowMassFrac;
				args.moveSpeedMultAdd += fac;
				args.attackSpeedMultAdd += fac;
            }
		}

		private void HealthComponent_TakeDamage(On.RoR2.HealthComponent.orig_TakeDamage orig, HealthComponent self, DamageInfo damageInfo) {
			if(self && self.body) {
				var count = GetCount(self.body);
				var cpt = self.GetComponent<SkeinTracker>();
				if(count > 0 && cpt) {
					var fac = 1f - (1f - Mathf.Pow(highMassFrac, count)) * cpt.GetResistanceScalar();
					damageInfo.damage *= fac;
					if(damageInfo.canRejectForce)
						damageInfo.force *= fac;
					cpt.ForceResetStopped();
                }
            }
			orig(self, damageInfo);
		}
	}

	[RequireComponent(typeof(CharacterBody))]
	public class SkeinTracker : MonoBehaviour {
		const float MOVING_TICK_RATE = 0.5f;

		float movingStopwatch = 0f;
		float shortNotMovingStopwatch = 0f;
		float tickStopwatch = 0f;
		bool isStopped = false;

		CharacterBody body;

		public float GetMovementScalar() {
			if(isStopped) return 0;
			return Mathf.Clamp01(movingStopwatch / Skein.instance.massChangeDuration);
        }

		public float GetResistanceScalar() {
			if(!isStopped) return 0;
			return Mathf.Clamp01(shortNotMovingStopwatch / Skein.instance.massChangeDuration);
		}

		public void ForceResetStopped() {
			shortNotMovingStopwatch = 0f;
        }

		void Awake() {
			body = GetComponent<CharacterBody>();
        }

		void FixedUpdate() {
			if(!body) return;
			float minMove = 0.1f * Time.fixedDeltaTime;
			if((body.transform.position - body.previousPosition).sqrMagnitude <= minMove * minMove) {
				shortNotMovingStopwatch += Time.fixedDeltaTime;
				if(!isStopped) {
					if(shortNotMovingStopwatch > Skein.instance.moveGracePeriod) {
						movingStopwatch = 0f;
						isStopped = true;
						body.statsDirty = true;
					} else movingStopwatch += Time.fixedDeltaTime;
                }
			} else {
				isStopped = false;
				movingStopwatch += Time.fixedDeltaTime;
				shortNotMovingStopwatch = 0f;
			}

			if(!isStopped) {
				tickStopwatch -= Time.fixedDeltaTime;
				if(tickStopwatch <= 0f) {
					tickStopwatch = MOVING_TICK_RATE;
					body.statsDirty = true;
				}
			}
        }
    }
}