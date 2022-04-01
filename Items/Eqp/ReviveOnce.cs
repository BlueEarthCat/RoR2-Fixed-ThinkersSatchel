﻿using RoR2;
using UnityEngine;
using TILER2;
using System.Linq;
using RoR2.Navigation;
using UnityEngine.Networking;

namespace ThinkInvisible.TinkersSatchel {
    public class ReviveOnce : Equipment<ReviveOnce> {

        ////// Equipment Data //////

        public override string displayName => "Command Terminal";
        public override bool isLunar => false;
        public override bool canBeRandomlyTriggered => false;
        public override float cooldown { get; protected set; } = 10f;

        protected override string GetNameString(string langid = null) => displayName;
        protected override string GetPickupString(string langid = null) => "Revive an ally or summon a drone. Consumed on use.";
        protected override string GetDescString(string langid = null) => $"Revives one survivor at random, calling them down in a drop pod. If no survivors are dead, the drop pod will contain a random drone instead. Will be consumed on use.";
        protected override string GetLoreString(string langid = null) => $"";



        ////// Other Fields/Properties //////

        GameObject[] droneMasterPrefabs;



        ////// TILER2 Module Setup //////

        public ReviveOnce() {
            modelResource = TinkersSatchelPlugin.resources.LoadAsset<GameObject>("Assets/TinkersSatchel/Prefabs/ReviveOnce.prefab");
            iconResource = TinkersSatchelPlugin.resources.LoadAsset<Sprite>("Assets/TinkersSatchel/Textures/Icons/reviveOnceIcon.png");
        }

        public override void SetupAttributes() {
            base.SetupAttributes();
            droneMasterPrefabs = new[] {
                LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterMasters/EquipmentDroneMaster"),
                LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterMasters/Drone1Master"),
                LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterMasters/Drone2Master"),
                LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterMasters/FlameDroneMaster"),
                LegacyResourcesAPI.Load<GameObject>("Prefabs/CharacterMasters/DroneMissileMaster")
            };
        }

        public override void Install() {
            base.Install();
        }

        public override void Uninstall() {
            base.Uninstall();
        }



        ////// Hooks //////

        protected override bool PerformEquipmentAction(EquipmentSlot slot) {
            var candidates = CharacterMaster.readOnlyInstancesList.Where(x => x.IsDeadAndOutOfLivesServer() && x.teamIndex == TeamIndex.Player);

            GameObject obj;
            GameObject podPrefab = LegacyResourcesAPI.Load<GameObject>("Prefabs/NetworkedObjects/RoboCratePod");

            var nodeGraph = SceneInfo.instance.GetNodeGraph(MapNodeGroup.GraphType.Ground);
            var nodeInd = nodeGraph.FindClosestNodeWithFlagConditions(slot.transform.position, HullClassification.Human, NodeFlags.None, NodeFlags.None, false);
            Vector3 nodePos = slot.transform.position;
            Quaternion nodeRot = Quaternion.identity;
            if(nodeGraph.GetNodePosition(nodeInd, out nodePos)) {
                var targPos = slot.transform.position;
                targPos.y = nodePos.y;
                nodeRot = Util.QuaternionSafeLookRotation(nodePos - targPos);
            }

            if(candidates.Count() > 0) {
                var which = rng.NextElementUniform(candidates.ToArray());
                var newBody = which.Respawn(nodePos, nodeRot);
                if(!newBody) return false;
                obj = newBody.gameObject;
            } else {
                var which = rng.NextElementUniform(droneMasterPrefabs);
                var summon = new MasterSummon {
                    masterPrefab = which,
                    position = nodePos,
                    rotation = nodeRot,
                    summonerBodyObject = slot.characterBody ? slot.characterBody.gameObject : null,
                    ignoreTeamMemberLimit = true,
                    useAmbientLevel = new bool?(true)
                }.Perform();
                if(!summon) return false;
                obj = summon.GetBodyObject();
                if(!obj) return false;
                if(obj.name == "EquipmentDroneBody(Clone)") {
                    var droneInv = obj.GetComponent<Inventory>();
                    if(droneInv) {
                        var randomEqp = rng.NextElementUniform(RoR2.Artifacts.EnigmaArtifactManager.validEquipment); 
                        droneInv.SetEquipment(new EquipmentState(randomEqp, Run.FixedTimeStamp.negativeInfinity, 1), 0);
                    }
                }
            }

            if(!obj) return false;
            var objBody = obj.GetComponent<CharacterBody>();

            var podObj = GameObject.Instantiate(podPrefab, nodePos, nodeRot);
            var podSeat = podObj.GetComponent<VehicleSeat>();
            if(podSeat) {
                podSeat.AssignPassenger(obj);
            } else {
                TinkersSatchelPlugin._logger.LogError($"Pod {podObj} spawned for revived prefab {obj} has no seat!");
            }
            NetworkServer.Spawn(podObj);
            objBody.SetBodyStateToPreferredInitialState();

            slot.inventory.SetEquipmentIndex(EquipmentIndex.None);
            return true;
        }
    }
}