using BepInEx;
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
    [BepInPlugin(__GUID__, __NAME__, "1.1.5")]
    public class SplitterOverBelt : BaseUnityPlugin
    {
        public const string __NAME__ = "SplitterOverBelt";
        public const string __GUID__ = "com.hetima.dsp." + __NAME__;

        protected static List<EntityData> _beltEntities = new List<EntityData>(20);
        protected static bool _doMod = false;
        protected static string _warningString;

        new internal static ManualLogSource Logger;
        void Awake()
        {
            Logger = base.Logger;
            //Logger.LogInfo("Awake");

            new Harmony(__GUID__).PatchAll(typeof(Patch));
        }

        public static void Log(string str)
        {
            Logger.LogInfo(str);
        }

        private static void AddBeltEntity(EntityData e)
        {
            bool add = true;
            for (int i = _beltEntities.Count - 1; i >= 0; i--)
            {
                EntityData e2 = _beltEntities[i];
                if (e2.beltId == e.beltId)
                {
                    add = false;
                    break;
                }
            }
            if (add)
            {
                _beltEntities.Add(e);
            }
        }

        private static void ValidateBelt(BuildTool_Click tool, BuildPreview buildPreview, Pose slotPose, EntityData entityData, out bool validBelt, out bool isOutput)
        {
            isOutput = false;
            validBelt = false;

            //Pose pose = tool.GetObjectPose(tool.buildPreviews[0].objId);
            Pose pose = new Pose(buildPreview.lpos, buildPreview.lrot);
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

        //位置は検証済み
        //中心は削除済み
        //角度は考慮せずベルトの端かどうかだけ調べる版
        private static void ValidateBelt2(BuildTool_Click tool, BuildPreview buildPreview, Pose slotPose, EntityData entityData, out bool validBelt, out bool isOutput)
        {
            isOutput = false;
            validBelt = false;

            int objId = entityData.id;
            bool hasOutput = false;
            bool hasInput = false;
            for (int i = 0; i < 4; i++)
            {
                tool.factory.ReadObjectConn(objId, i, out bool isOutput2, out int otherId, out int _);
                if (otherId != 0)
                {
                    if (isOutput2)
                    {
                        hasInput = true;
                    }
                    else
                    {
                        hasOutput = true;
                    }
                }
            }
            if (hasInput == hasOutput)
            {
                validBelt = false;
            }
            else if (hasInput)
            {
                isOutput = true;
                validBelt = true;
            }
            else if (hasOutput)
            {
                isOutput = false;
                validBelt = true;
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

        public static void ConnectBelts(BuildTool_Click tool, List<BuildPreview> buildPreviews)
        {

            BuildPreview buildPreview = buildPreviews[0];
            Pose[] poses = buildPreview.desc.portPoses;

            Vector3[] snappedPos = new Vector3[poses.Length]; //splitterに繋がるベルト位置
            Vector3[] slotPos = new Vector3[poses.Length]; //splitterの中にある終端ベルト位置
            BeltData[] beltData = new BeltData[poses.Length];
            float[] gridSize = new float[poses.Length];
            Quaternion lrot = buildPreview.lrot;
            //CreatePrebuilds() でこうやってる 特に変化はない気がする
            if (buildPreview.isConnNode)
            {
                lrot = Maths.SphericalRotation(buildPreview.lpos, 0f);
            }
            for (int i = 0; i < poses.Length; i++)
            {
                //グリッド幅の計算 これで良いのか？ 正しい数値が返ってきているようではある
                gridSize[i] = tool.actionBuild.planetAux.activeGrid.CalcLocalGridSize(buildPreview.lpos, lrot * poses[i].forward);
                Vector3 p = buildPreview.lpos + lrot * (poses[i].position + poses[i].forward * gridSize[i]);
                snappedPos[i] = tool.actionBuild.planetAux.Snap(p, false);
                slotPos[i] = buildPreview.lpos + lrot * (poses[i].position);
            }

            //gather
            for (int k = _beltEntities.Count - 1; k >= 0; k--)
            {
                EntityData e = _beltEntities[k];
                for (int i = 0; i < snappedPos.Length; i++)
                {
                    //snapして計算すると斜めのベルトも繋がりやすくなった
                    Vector3 spos = tool.actionBuild.planetAux.Snap(e.pos, false);
                    float dist = (spos - snappedPos[i]).sqrMagnitude;
                    float limit = gridSize[i] * 0.17f;
                    if (dist < (limit * limit))
                    {
                        //通常ありえないが非常に近い場所にベルトが存在したら削除する snapするようにしたので可能性が高くなった
                        if (beltData[i].validBelt)
                        {
                            tool.actionBuild.DoDismantleObject(e.id);
                            _beltEntities.RemoveAt(k);
                            break;
                        }

                        bool validBelt;
                        bool isOutput;
                        Quaternion rotation = poses[i].rotation;

                        ValidateBelt2(tool, buildPreview, poses[i], e, out validBelt, out isOutput);
                        if (validBelt)
                        {
                            beltData[i].splitterSlot = i;
                            beltData[i].slotPos = slotPos[i];
                            beltData[i].slotRot = rotation;
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

                BuildPreview beltBuildPreview = new BuildPreview();
                PrefabDesc desc = LDB.models.Select(beltData[i].entityData.modelIndex)?.prefabDesc;
                beltBuildPreview.ResetAll();
                beltBuildPreview.item = LDB.items.Select(beltData[i].entityData.protoId);
                beltBuildPreview.desc = desc;

                beltBuildPreview.lpos = beltData[i].slotPos;
                beltBuildPreview.lpos2 = Vector3.zero;
                beltBuildPreview.lrot = lrot * beltData[i].slotRot;
                beltBuildPreview.lrot2 = Quaternion.identity;
                beltBuildPreview.needModel = false;
                beltBuildPreview.isConnNode = false;//trueにすべき？
                beltBuildPreview.outputOffset = 0;


                //var connectionPrebuildData = new PrebuildData
                //{
                //    protoId = beltData[i].entityData.protoId,
                //    modelIndex = beltData[i].entityData.modelIndex,
                //    pos = beltData[i].slotPos,
                //    pos2 = Vector3.zero,
                //    rot = lrot * beltData[i].slotRot,
                //    rot2 = Quaternion.identity,
                //};

                //var id = (int)beltData[i].entityData.protoId;
                //var count = 1;
                //tool.player.package.TakeTailItems(ref id, ref count);

                //var objId = -tool.factory.AddPrebuildDataWithComponents(connectionPrebuildData);
                var otherBeltSlot = 1; //-1だと4～11のうち空いてる最小スロットになる？
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

                //接続

                //これが実行される
                //this.factory.WriteObjectConn(bp.objId, bp.outputFromSlot, true, bp.output.objId, bp.outputToSlot);
                //this.factory.WriteObjectConn(bp.objId, bp.inputToSlot, false, bp.input.objId, bp.inputFromSlot);

                if (beltData[i].isOutput)
                {
                    //以前実行してたコード
                    //tool.factory.WriteObjectConn(objId, 0, true, beltData[i].entityData.id, otherBeltSlot);
                    //tool.factory.WriteObjectConn(objId, 1, false, buildPreview.objId, beltData[i].splitterSlot);
                    beltBuildPreview.output = null;
                    beltBuildPreview.outputObjId = beltData[i].entityData.id;
                    beltBuildPreview.outputFromSlot = 0;
                    beltBuildPreview.outputToSlot = otherBeltSlot;

                    beltBuildPreview.input = buildPreview;
                    beltBuildPreview.inputObjId = 0;
                    beltBuildPreview.inputToSlot = 1;
                    beltBuildPreview.inputFromSlot = beltData[i].splitterSlot;
                }
                else
                {
                    //以前実行してたコード
                    //tool.factory.WriteObjectConn(objId, 0, true, buildPreview.objId, beltData[i].splitterSlot);
                    //tool.factory.WriteObjectConn(objId, 1, false, beltData[i].entityData.id, otherBeltSlot);
                    beltBuildPreview.output = buildPreview;
                    beltBuildPreview.outputObjId = 0;
                    beltBuildPreview.outputFromSlot = 0;
                    beltBuildPreview.outputToSlot = beltData[i].splitterSlot;

                    beltBuildPreview.input = null;
                    beltBuildPreview.inputObjId = beltData[i].entityData.id;
                    beltBuildPreview.inputToSlot = 1;
                    beltBuildPreview.inputFromSlot = otherBeltSlot;
                }

                buildPreviews.Add(beltBuildPreview);
            }
        }

        public static void DeleteConfusedBelts(BuildTool_Click tool, BuildPreview buildPreview)
        {
            if (_beltEntities.Count > 0)
            {
                //中心にあるベルトを削除
                float gridSize = tool.actionBuild.planetAux.activeGrid.CalcLocalGridSize(buildPreview.lpos, buildPreview.lrot * Vector3.up);
                Vector3 topPos = buildPreview.lpos + buildPreview.lrot * (Vector3.up * gridSize);
                //中心からの距離判定 snapさせるのであまり意味はない
                float limit = PlanetGrid.kAltGrid * 0.3f;
                float sqrLlimit = (limit * limit);

                for (int i = _beltEntities.Count - 1; i >= 0; i--)
                {
                    EntityData e = _beltEntities[i];
                    if (e.beltId > 0)
                    {
                        Vector3 spos = tool.actionBuild.planetAux.Snap(e.pos, false);
                        float dist = (spos - buildPreview.lpos).sqrMagnitude;
                        float dist2 = (spos - topPos).sqrMagnitude;
                        if (dist < sqrLlimit || dist2 < sqrLlimit)
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

        public static bool CanBuildSplitter(BuildTool_Click tool, BuildPreview buildPreview)
        {
            //中心にあるベルトかどうか調べるための情報
            float gridSize = tool.actionBuild.planetAux.activeGrid.CalcLocalGridSize(buildPreview.lpos, buildPreview.lrot * Vector3.up);
            Vector3 topPos = buildPreview.lpos + buildPreview.lrot * (Vector3.up * gridSize);
            float limit = gridSize * 0.3f;
            float sqrLlimit = (limit * limit);

            //繋がるベルトかどうか調べるための情報
            Vector3[] snappedPos = new Vector3[buildPreview.desc.portPoses.Length]; //splitterに繋がるベルト位置
            float[] gridSizes = new float[buildPreview.desc.portPoses.Length];
            Quaternion lrot = buildPreview.lrot;
            //CreatePrebuilds() でこうやってる 特に変化はない気がする
            if (buildPreview.isConnNode)
            {
                lrot = Maths.SphericalRotation(buildPreview.lpos, 0f);
            }
            for (int i = 0; i < buildPreview.desc.portPoses.Length; i++)
            {
                Pose pose = buildPreview.desc.portPoses[i];
                gridSizes[i] = tool.actionBuild.planetAux.activeGrid.CalcLocalGridSize(buildPreview.lpos, lrot * pose.forward);
                Vector3 p = buildPreview.lpos + lrot * (pose.position + pose.forward * gridSize);
                snappedPos[i] = tool.actionBuild.planetAux.Snap(p, false);
                //slotPos[i] = buildPreview.lpos + lrot * (poses[i].position);
            }


            for (int i = 0; i < BuildToolAccess.TmpColsLength(); i++)
            {
                Collider collider = BuildToolAccess.TmpCols[i];
                ColliderData colliderData;
                //colliderData取れないケースがあった
                if (tool.planet.physics.GetColliderData(collider, out colliderData))
                {
                    if (colliderData.objType == EObjectType.Entity) {
                        int eid = colliderData.objId;
                        EntityData e = tool.planet.factory.entityPool[eid];
                        if (e.beltId == 0)
                        {
                            return false;
                        }
                        else
                        {
                            Vector3 spos = tool.actionBuild.planetAux.Snap(e.pos, false);
                            float dist = (spos - buildPreview.lpos).sqrMagnitude;
                            float dist2 = (spos - topPos).sqrMagnitude;
                            if (dist < sqrLlimit)
                            {
                                //中心 0.5もこっちでヒットする(Snapしてるせいで)
                                continue;
                            }
                            else if (dist2 < sqrLlimit)
                            {
                                //中心 上
                                if (buildPreview.item.ModelCount > 0 && tool.modelOffset % buildPreview.item.ModelCount == 0)
                                {
                                    //中心と繋がっているベルトのcolliderDataを取れないときの保険
                                    return false;
                                }
                                continue;
                            }
                            else
                            {
                                //中心ではない
                                //繋がるものも含まれる
                                bool validBelt = false;
                                for (int j = 0; j < buildPreview.desc.portPoses.Length; j++)
                                {
                                    float dist3 = (spos - snappedPos[j]).sqrMagnitude;
                                    float limit2 = gridSizes[j] * 0.17f;
                                    if (dist3 < (limit2 * limit2))
                                    {
                                        ValidateBelt(tool, buildPreview, buildPreview.desc.portPoses[j], e, out validBelt, out bool isOutput);
                                        if (validBelt)
                                        {
                                            if (buildPreview.desc.isPiler)
                                            {
                                                if (j == 0 && !isOutput || j == 1 && isOutput)
                                                {
                                                    string test = isOutput.ToString();
                                                    _warningString = "(Piler is facing the wrong way)";
                                                }
                                            }
                                            
                                            break;
                                        }
                                    }
                                }
                                if (validBelt)
                                {
                                    continue;
                                }
                                _warningString = null;
                                return false;
                            }
                        }
                    }
                    //else if(colliderData.objType == EObjectType.Prebuild)
                    //{
                        //PrebuildData d = tool.factory.prebuildPool[-colliderData.objId];
                        //UIRealtimeTip.Popup("" + d.recipeId, false, 0);
                        //PrefabDesc desc = tool.GetPrefabDesc(colliderData.objId);
                        //if (!tool.ObjectIsBelt(colliderData.objId))
                        //{
                        //}
                        //return false;
                    //}
                    else //Vein etc
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static bool GatherNearBelts(BuildTool_Click tool, BuildPreview buildPreview)
        {
            bool result = false;
            _beltEntities.Clear();

            if (buildPreview?.desc == null || tool.actionBuild?.planetAux?.activeGrid == null || tool.actionBuild?.nearcdLogic == null)
            {
                return false;
            }
            float gridSize = tool.actionBuild.planetAux.activeGrid.CalcLocalGridSize(buildPreview.lpos, buildPreview.lrot * buildPreview.desc.portPoses[0].forward);
            Vector3 calcPos = buildPreview.lpos + buildPreview.lrot * (Vector3.up * gridSize / 2);

            BuildToolAccess.nearObjectCount = tool.actionBuild.nearcdLogic.GetBuildingsInAreaNonAlloc(calcPos, gridSize * 1.4f, BuildToolAccess.nearObjectIds, false);

            for (int i = 0; i < BuildToolAccess.nearObjectCount; i++)
            {
                int eid = BuildToolAccess.nearObjectIds[i];
                if (eid > 0)
                {
                    EntityData e = tool.planet.factory.entityPool[eid];
                    if (e.beltId != 0)
                    {
                        result = true;
                        AddBeltEntity(e);
                    }
                }
            }
            return result;
        }

        static class Patch
        {

            [HarmonyPostfix, HarmonyPatch(typeof(BuildTool_Click), "CheckBuildConditions"), HarmonyAfter("dsp.nebula-multiplayer")]
            public static void BuildTool_Click_CheckBuildConditions_Postfix(BuildTool_Click __instance, ref bool __result)
            {

                _doMod = false;
                _warningString = null;
                //if (VFInput.control)
                //{
                //    return;
                //}
                if (__instance.buildPreviews.Count == 1)
                {
                    BuildPreview buildPreview = __instance.buildPreviews[0];
                    if (buildPreview.desc.isSplitter || buildPreview.desc.isPiler)
                    {
                        if (buildPreview.condition == EBuildCondition.Ok)
                        {
                            _doMod = true;
                        }
                        else if (buildPreview.condition == EBuildCondition.Collide)
                        {
                            bool result = CanBuildSplitter(__instance, buildPreview);
                            if (result)
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
            }

            [HarmonyPrefix, HarmonyPatch(typeof(BuildTool_Click), "CreatePrebuilds"), HarmonyBefore("dsp.nebula-multiplayer")]
            public static void BuildTool_Click_CreatePrebuilds_Prefix(BuildTool_Click __instance)
            {

                if (_doMod && __instance.buildPreviews.Count == 1)
                {
                    _doMod = false;
                    BuildPreview buildPreview = __instance.buildPreviews[0];
                    if ((buildPreview.desc.isSplitter || buildPreview.desc.isPiler ) && GatherNearBelts(__instance, buildPreview))
                    {
                        DeleteConfusedBelts(__instance, buildPreview);
                    }
                    if (_beltEntities.Count > 0)
                    {
                        if ((buildPreview.desc.isSplitter || buildPreview.desc.isPiler) && buildPreview.condition == EBuildCondition.Ok)
                        {
                            ConnectBelts(__instance, __instance.buildPreviews);
                        }
                        _beltEntities.Clear();
                    }
                }
            }

            [HarmonyPostfix, HarmonyPatch(typeof(BuildTool_Click), "ConfirmOperation")]
            public static void BuildTool_Click_ConfirmOperation_Postfix(BuildTool_Click __instance, ref bool __result)
            {
                //CreatePrebuilds しなかった場合 _doMod の状態がリセットされない
                //普段は問題ないが Nebula が直接 CreatePrebuilds を呼ぶので前の状態が残っているとまずい
                if (!__result && _doMod)
                {
                    if (_warningString != null)
                    {
                        __instance.actionBuild.model.cursorText += " <color=#fbce32ff>" + _warningString + "</color>";
                        _warningString = null;
                    }

                    _doMod = false;
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
            public static Collider[] TmpCols
            {
                get
                {
                    return _tmp_cols;
                }
            }

            public static int[] nearObjectIds
            {
                get
                {
                    return _nearObjectIds;
                }
                
            }
            public static int nearObjectCount
            {
                get
                {
                    return _nearObjectCount;
                }
                set
                {
                    _nearObjectCount = value;
                }
            }

        }

    }
}
