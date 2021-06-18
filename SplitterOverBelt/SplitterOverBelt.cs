﻿using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;


//
// referenced much of codes from [SplitterOverConveyor](https://github.com/KingEnderBrine/-DSP-SplitterOverConveyor)
//

namespace SplitterOverBelt
{
    [BepInPlugin(__GUID__, __NAME__, "1.0.0")]
    public class SplitterOverBelt : BaseUnityPlugin
    {
        public const string __NAME__ = "SplitterOverBelt";
        public const string __GUID__ = "com.hetima.dsp." + __NAME__;

        protected static List<EntityData> _beltEntities = new List<EntityData>(20);
        protected static bool _doMod = false;

        new internal static ManualLogSource Logger;
        void Awake()
        {
            Logger = base.Logger;
            //Logger.LogInfo("Awake");

            new Harmony(__GUID__).PatchAll(typeof(Patch));
        }


        private static void ValidateBelt(BuildTool_Click tool, Pose slotPose, EntityData entityData, out bool validBelt, out bool isOutput)
        {
            isOutput = false;
            validBelt = false;

            Pose pose = tool.GetObjectPose(tool.buildPreviews[0].objId);
            Vector3 slotDirection = pose.rotation * slotPose.forward;

            BeltComponent belt = tool.factory.cargoTraffic.beltPool[entityData.beltId];
            CargoPath cargoPath = tool.factory.cargoTraffic.GetCargoPath(belt.segPathId);
            Quaternion beltInputRotation = cargoPath.pointRot[belt.segIndex];
            Quaternion beltOutputRotation = cargoPath.pointRot[belt.segIndex + belt.segLength - 1];

            bool beltIsStraight = Vector3.Dot(beltInputRotation * Vector3.forward, beltOutputRotation * Vector3.forward) > 0.5;

            if (beltIsStraight)
            {
                float dot = Vector3.Dot(beltInputRotation * Vector3.forward, slotDirection);
                if (Math.Abs(dot) > 0.5F)
                {
                    validBelt = true;
                    isOutput = dot > 0.5F;
                    return;
                }
                return;
            }

            if (Vector3.Dot(beltInputRotation * Vector3.forward, slotDirection) > 0.5F)
            {
                validBelt = true;
                isOutput = true;
                return;
            }
            if (Vector3.Dot(beltOutputRotation * Vector3.forward, slotDirection) < -0.5F)
            {
                validBelt = true;
                isOutput = false;
                return;
            }
        }

        public struct BeltData
        {
            public bool validBelt;
            public int splitterSlot;
            public Vector3 slotPos;
            public Quaternion slotRot;
            public bool isOutput;
            public EntityData entityData;
        }

        public static void connectBelts(BuildTool_Click tool, BuildPreview buildPreview)
        {
            Pose[] poses = buildPreview.desc.portPoses;

            Vector3[] snappedPos = new Vector3[poses.Length]; //splitterに繋がるベルト位置
            Vector3[] slotPos = new Vector3[poses.Length]; //splitterの中にある終端ベルト位置
            BeltData[] beltData = new BeltData[poses.Length];
            for (int i = 0; i < poses.Length; i++)
            {
                var p = buildPreview.lpos + buildPreview.lrot * (poses[i].position + poses[i].forward * PlanetGrid.kAltGrid);
                snappedPos[i] = tool.actionBuild.planetAux.Snap(p, false);
                slotPos[i] = buildPreview.lpos + buildPreview.lrot * (poses[i].position);
            }

            //gather
            float limit = PlanetGrid.kAltGrid * 0.1f;
            for (int k = _beltEntities.Count - 1; k >= 0; k--)
            {
                EntityData e = _beltEntities[k];
                for (int i = 0; i < snappedPos.Length; i++)
                {
                    float dist = Vector3.Distance(e.pos, snappedPos[i]);
                    if (dist < limit)
                    {
                        //通常ありえないが非常に近い場所にベルトが存在したら削除する
                        if (beltData[i].validBelt)
                        {
                            tool.actionBuild.DoDismantleObject(e.id);
                            _beltEntities.RemoveAt(k);
                            break;
                        }
                        bool validBelt;
                        bool isOutput;
                        ValidateBelt(tool, poses[i], e, out validBelt, out isOutput);
                        if (validBelt)
                        {
                            beltData[i].splitterSlot = i;
                            beltData[i].slotPos = slotPos[i];
                            beltData[i].slotRot = poses[i].rotation;
                            beltData[i].entityData = e;
                            beltData[i].validBelt = validBelt;
                            beltData[i].isOutput = isOutput;
                        }
                        break;
                    }
                }
            }

            //connect
            for (int i = 0; i < snappedPos.Length; i++)
            {
                if (!beltData[i].validBelt)
                {
                    continue;
                }

                var connectionPrebuildData = new PrebuildData
                {
                    protoId = beltData[i].entityData.protoId,
                    modelIndex = beltData[i].entityData.modelIndex,
                    pos = beltData[i].slotPos,
                    pos2 = Vector3.zero,
                    rot = buildPreview.lrot * beltData[i].slotRot,
                    rot2 = Quaternion.identity,
                };

                var id = (int)beltData[i].entityData.protoId;
                var count = 1;
                tool.player.package.TakeTailItems(ref id, ref count);

                var objId = -tool.factory.AddPrebuildDataWithComponents(connectionPrebuildData);
                var otherBeltSlot = -1;
                if (!beltData[i].isOutput)
                {
                    otherBeltSlot = 0;
                }
                else
                {
                    for (var j = 1; j < 4; j++)
                    {
                        tool.factory.ReadObjectConn(beltData[i].entityData.id, j, out _, out var otherObjId, out _);
                        if (otherObjId == 0)
                        {
                            otherBeltSlot = j;
                            break;
                        }
                    }
                }
                tool.factory.WriteObjectConn(objId, beltData[i].isOutput ? 1 : 0, !beltData[i].isOutput, buildPreview.objId, beltData[i].splitterSlot);
                tool.factory.WriteObjectConn(objId, beltData[i].isOutput ? 0 : 1, beltData[i].isOutput, beltData[i].entityData.id, otherBeltSlot);
            }
        }

        public static void deleteConfusedBelts(BuildTool_Click tool, BuildPreview buildPreview)
        {
            if (buildPreview.desc.hasBuildCollider && _beltEntities.Count > 0)
            {
                //中心にあるベルトを削除
                Vector3 topPos = buildPreview.lpos + buildPreview.lrot * (Vector3.up * PlanetGrid.kAltGrid);
                float limit = PlanetGrid.kAltGrid * 0.88f; //斜めに敷かれたベルトをなるべく消すため広めに取る
                for (int i = _beltEntities.Count - 1; i >= 0; i--)
                {
                    EntityData e = _beltEntities[i];
                    if (e.beltId > 0)
                    {
                        float dist = Vector3.Distance(e.pos, buildPreview.lpos);
                        float dist2 = Vector3.Distance(e.pos, topPos);
                        if (dist < limit || dist2 < limit)
                        {
                            tool.actionBuild.DoDismantleObject(e.id);
                            _beltEntities.RemoveAt(i);
                        }
                    }
                    else
                    {
                        _beltEntities.RemoveAt(i);
                    }
                }
                //孤立したベルトを削除
                BeltComponent[] beltPool = tool.factory.cargoTraffic.beltPool;
                for (int i = _beltEntities.Count - 1; i >= 0; i--)
                {
                    EntityData e = _beltEntities[i];
                    BeltComponent b = beltPool[e.beltId];
                    if (b.id == e.beltId) {
                        CargoPath cargoPath = tool.factory.cargoTraffic.GetCargoPath(b.segPathId);
                        if (cargoPath.belts.Count == 1)
                        {
                            tool.actionBuild.DoDismantleObject(e.id);
                            _beltEntities.RemoveAt(i);
                        }
                    }
                }
            }
        }

        public static bool canBuildSplitter(BuildTool_Click tool, BuildPreview buildPreview, bool gather = false)
        {
            if (buildPreview.desc.hasBuildCollider)
            {
                if (gather) _beltEntities.Clear();
                for (int i = 0; i < BuildToolAccess.TmpColsLength(); i++)
                {
                    Collider collider = BuildToolAccess.TmpCols()[i];
                    ColliderData colliderData;
                    if (tool.planet.physics.GetColliderData(collider, out colliderData) && colliderData.objType == EObjectType.Entity)
                    {
                        int eid = colliderData.objId;
                        EntityData e = tool.planet.factory.entityPool[eid];
                        if(e.beltId <= 0)
                        {
                            if (gather) _beltEntities.Clear();
                            return false;
                        }
                        else
                        {
                            if(gather) _beltEntities.Add(e);
                        }
                    }
                }
                return true;
            }
            return false;
        }



        static class Patch
        {
            internal static bool _unpatched = false;

            public static void unloadConflictedMods()
            {
                // SmolSplitters とコンフリクトするのでオフにする
                // Harmonyの作り方がおかしいので ID が "harmony-auto-?????" などとなっており面倒くさい
                var original = AccessTools.Method(typeof(PrefabDesc), "ReadPrefab");
                if (original is null) return;
                var patches = Harmony.GetPatchInfo(original);
                if (patches is null) return;
                foreach (var patch in patches.Postfixes)
                {
                    if (patch.PatchMethod.DeclaringType.FullName == "Mo3sDspMods.SmolSplittersPlugin+SplitterColliderPatch")
                    {
                        new Harmony(patch.owner).UnpatchSelf();
                        Logger.LogInfo("Unpatch SmolSplitters");
                        break;
                    }
                }
            }

            [HarmonyPrefix, HarmonyPatch(typeof(VFPreload), "Start")]
            public static void PatchForUnloadConflictedMod()
            {
                if (!_unpatched)
                {
                    _unpatched = true;
                    unloadConflictedMods();
                }
            }

            [HarmonyPostfix, HarmonyPatch(typeof(BuildTool_Click), "CheckBuildConditions")]
            public static void BuildTool_Click_CheckBuildConditions_Postfix(BuildTool_Click __instance, ref bool __result)
            {
                _doMod = false;
                if (!__result && __instance.buildPreviews.Count == 1)
                {
                    BuildPreview buildPreview = __instance.buildPreviews[0];
                    if (buildPreview.desc.isSplitter && buildPreview.condition == EBuildCondition.Collide)
                    {
                        if (canBuildSplitter(__instance, buildPreview))
                        {
                            __result = true;
                            buildPreview.condition = EBuildCondition.Ok;
                            __instance.actionBuild.model.cursorText = buildPreview.conditionText;
                            __instance.actionBuild.model.cursorState = 0;
                            if (!VFInput.onGUI)
                            {
                                UICursor.SetCursor(ECursor.Default);
                            }
                            _doMod = true;
                        }
                    }
                }
            }

            [HarmonyPrefix, HarmonyPatch(typeof(BuildTool_Click), "CreatePrebuilds")]
            public static void BuildTool_Click_CreatePrebuilds_Prefix(BuildTool_Click __instance)
            {
                if (_doMod && __instance.buildPreviews.Count == 1)
                {
                    BuildPreview buildPreview = __instance.buildPreviews[0];
                    if (buildPreview.desc.isSplitter && canBuildSplitter(__instance, buildPreview, true))
                    {
                        deleteConfusedBelts(__instance, buildPreview);
                    }
                }
            }

            [HarmonyPostfix, HarmonyPatch(typeof(BuildTool_Click), "CreatePrebuilds")]
            public static void BuildTool_Click_CreatePrebuilds_Postfix(BuildTool_Click __instance)
            {
                if (_doMod && __instance.buildPreviews.Count == 1 && _beltEntities.Count > 0)
                {
                    BuildPreview buildPreview = __instance.buildPreviews[0];
                    if (buildPreview.desc.isSplitter && buildPreview.condition == EBuildCondition.Ok)
                    {
                        connectBelts(__instance, buildPreview);
                    }
                    _beltEntities.Clear();
                }
            }
        }

        public class BuildToolAccess : BuildTool
        {
            public static int TmpColsLength()
            {
                int result = 0;
                for (int i = 0; i < _tmp_cols.Length; i++)
                {
                    if (_tmp_cols[i] != null)
                    {
                        result++;
                    }
                    else
                    {
                        break;
                    }
                }
                return result;
            }
            public static Collider[] TmpCols()
            {
                return _tmp_cols;
            }

        }

    }
}