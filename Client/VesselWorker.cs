using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DarkMultiPlayer
{
    public class VesselWorker
    {
        public bool workerEnabled;
        //Set just before the worker is enabled to ignore fake vessel destructions during KSP's start.
        public float gameSceneChangeTime;
        //Hooks enabled
        private bool registered;
        private Client parent;
        //Update frequency
        private const float VESSEL_PROTOVESSEL_UPDATE_INTERVAL = 30f;
        //Pack distances
        private const float PLAYER_UNPACK_THRESHOLD = 9000;
        private const float PLAYER_PACK_THRESHOLD = 10000;
        private const float NORMAL_UNPACK_THRESHOLD = 300;
        private const float NORMAL_PACK_THRESHOLD = 600;
        private const float SAFETY_BUBBLE_DISTANCE = 100;
        //Spectate stuff
        public const ControlTypes BLOCK_ALL_CONTROLS = ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.ACTIONS_ALL | ControlTypes.EVA_INPUT | ControlTypes.TIMEWARP | ControlTypes.MISC | ControlTypes.GROUPS_ALL | ControlTypes.CUSTOM_ACTION_GROUPS;
        private const string DARK_SPECTATE_LOCK = "DMP_Spectating";
        private const float UPDATE_SCREEN_MESSAGE_INTERVAL = 1f;
        private ScreenMessage spectateMessage;
        private float lastSpectateMessageUpdate;
        private ScreenMessage bannedPartsMessage;
        private string bannedPartsString;
        private float lastBannedPartsMessageUpdate;
        //Incoming queue
        private object createSubspaceLock = new object();
        private Dictionary<int, Queue<VesselRemoveEntry>> vesselRemoveQueue;
        private Dictionary<int, Queue<VesselProtoUpdate>> vesselProtoQueue;
        private Dictionary<int, Queue<VesselUpdate>> vesselUpdateQueue;
        private Dictionary<int, Queue<KerbalEntry>> kerbalProtoQueue;
        private Queue<ActiveVesselEntry> newActiveVessels;
        private List<string> serverVessels;
        private Dictionary<string, bool> vesselPartsOk;
        //Vessel state tracking
        private string lastVessel;
        private int lastPartCount;
        //Known kerbals
        private Dictionary<int, ProtoCrewMember> serverKerbals;
        public Dictionary<int, string> assignedKerbals;
        //Known vessels and last send/receive time
        private Dictionary<string, float> serverVesselsProtoUpdate;
        private Dictionary<string, float> serverVesselsPositionUpdate;
        //Track when the vessel was last controlled.
        private Dictionary<string, double> latestVesselUpdate;
        //Vessel id (key) owned by player (value) - Also read from PlayerStatusWorker
        public Dictionary<string, string> inUse;
        //Track spectating state
        private bool wasSpectating;
        private int spectateType;
        private bool destroyIsValid;
        private Vessel switchActiveVesselOnNextUpdate;

        public VesselWorker(Client parent)
        {
            this.parent = parent;
            Reset();
        }
        //Called from main
        public void FixedUpdate()
        {
            //GameEvents.debugEvents = true;
            if (workerEnabled && !registered)
            {
                registered = true;
                GameEvents.onVesselRecovered.Add(OnVesselRecovered);
                GameEvents.onVesselTerminated.Add(OnVesselTerminated);
                GameEvents.onVesselDestroy.Add(OnVesselDestroyed);
                GameEvents.onGameSceneLoadRequested.Add(OnGameSceneLoadRequested);
                GameEvents.onFlightReady.Add(OnFlightReady);
            }
            if (!workerEnabled && registered)
            {
                registered = false;
                GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
                GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
                GameEvents.onVesselDestroy.Remove(OnVesselDestroyed);
                GameEvents.onGameSceneLoadRequested.Remove(OnGameSceneLoadRequested);
                GameEvents.onFlightReady.Remove(OnFlightReady);
            }
            //If we aren't in a DMP game don't do anything.
            if (workerEnabled)
            {
                if (switchActiveVesselOnNextUpdate != null)
                {
                    FlightGlobals.ForceSetActiveVessel(switchActiveVesselOnNextUpdate);
                    switchActiveVesselOnNextUpdate = null;
                }

                //State tracking the active players vessels.
                UpdateOtherPlayersActiveVesselStatus();

                //Process new messages
                lock (createSubspaceLock)
                {
                    ProcessNewVesselMessages();
                }

                //Update the screen spectate message.
                UpdateOnScreenSpectateMessage();

                //Lock and unlock spectate state
                UpdateSpectateLock();

                //Tell other players we have taken a vessel
                UpdateActiveVesselStatus();

                //Check current vessel state
                CheckVesselHasChanged();

                //Send updates of needed vessels
                SendVesselUpdates();
            }
        }

        private void UpdateOtherPlayersActiveVesselStatus()
        {
            while (newActiveVessels.Count > 0)
            {
                ActiveVesselEntry ave = newActiveVessels.Dequeue();
                if (ave.vesselID != "")
                {
                    DarkLog.Debug("Player " + ave.player + " is now flying " + ave.vesselID);
                    SetInUse(ave.vesselID, ave.player);
                }
                else
                {
                    DarkLog.Debug("Player " + ave.player + " has released their vessel");
                    SetNotInUse(ave.player);
                }
            }
        }

        private void ProcessNewVesselMessages()
        {
            
            foreach (KeyValuePair<int, Queue<VesselRemoveEntry>> vesselRemoveSubspace in vesselRemoveQueue)
            {
                while (vesselRemoveSubspace.Value.Count > 0 ? (vesselRemoveSubspace.Value.Peek().planetTime < Planetarium.GetUniversalTime()) : false)
                {
                    VesselRemoveEntry removeVessel = vesselRemoveSubspace.Value.Dequeue();
                    RemoveVessel(removeVessel.vesselID);
                }
            }
            foreach (KeyValuePair<int, Queue<KerbalEntry>> kerbalProtoSubspace in kerbalProtoQueue)
            {
                while (kerbalProtoSubspace.Value.Count > 0 ? (kerbalProtoSubspace.Value.Peek().planetTime < Planetarium.GetUniversalTime()) : false)
                {
                    KerbalEntry kerbalEntry = kerbalProtoSubspace.Value.Dequeue();
                    LoadKerbal(kerbalEntry.kerbalID, kerbalEntry.kerbalNode);
                }
            }
            foreach (KeyValuePair<int, Queue<VesselProtoUpdate>> vesselProtoSubspace in vesselProtoQueue)
            {
                while (vesselProtoSubspace.Value.Count > 0 ? (vesselProtoSubspace.Value.Peek().planetTime < Planetarium.GetUniversalTime()) : false)
                {
                    VesselProtoUpdate vesselNode = vesselProtoSubspace.Value.Dequeue();
                    LoadVessel(vesselNode.vesselNode);
                }
            }
            foreach (KeyValuePair<int, Queue<VesselUpdate>> vesselUpdateSubspace in vesselUpdateQueue)
            {
                while (vesselUpdateSubspace.Value.Count > 0 ? (vesselUpdateSubspace.Value.Peek().planetTime < Planetarium.GetUniversalTime()) : false)
                {
                    VesselUpdate vesselUpdate = vesselUpdateSubspace.Value.Dequeue();
                    ApplyVesselUpdate(vesselUpdate);
                }
            }
        }

        private void UpdateOnScreenSpectateMessage()
        {
            
            if ((UnityEngine.Time.realtimeSinceStartup - lastSpectateMessageUpdate) > UPDATE_SCREEN_MESSAGE_INTERVAL)
            {
                lastSpectateMessageUpdate = UnityEngine.Time.realtimeSinceStartup;
                if (isSpectating)
                {
                    if (spectateMessage != null)
                    {
                        spectateMessage.duration = 0f;
                    }
                    switch (spectateType)
                    {
                        case 1:
                            spectateMessage = ScreenMessages.PostScreenMessage("This vessel is controlled by another player...", UPDATE_SCREEN_MESSAGE_INTERVAL * 2, ScreenMessageStyle.UPPER_CENTER);
                            break;
                        case 2:
                            spectateMessage = ScreenMessages.PostScreenMessage("This vessel has been changed in the future...", UPDATE_SCREEN_MESSAGE_INTERVAL * 2, ScreenMessageStyle.UPPER_CENTER);
                            break;
                    }
                }
                else
                {
                    if (spectateMessage != null)
                    {
                        spectateMessage.duration = 0f;
                        spectateMessage = null;
                    }
                }
            }
        }

        private void UpdateSpectateLock()
        {

            if (isSpectating != wasSpectating)
            {
                wasSpectating = isSpectating;
                if (isSpectating)
                {
                    DarkLog.Debug("Setting spectate lock");
                    InputLockManager.SetControlLock(BLOCK_ALL_CONTROLS, DARK_SPECTATE_LOCK);
                }
                else
                {
                    DarkLog.Debug("Releasing spectate lock");
                    InputLockManager.RemoveControlLock(DARK_SPECTATE_LOCK);
                }

            }
        }

        private void UpdateActiveVesselStatus()
        {
            bool isActiveVesselOk = FlightGlobals.ActiveVessel != null ? (FlightGlobals.ActiveVessel.loaded && !FlightGlobals.ActiveVessel.packed) : false;
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && isActiveVesselOk)
            {
                if (!isSpectating)
                {
                    if (!inUse.ContainsKey(FlightGlobals.ActiveVessel.id.ToString()))
                    {
                        //Nobody else is flying the vessel - let's take it
                        parent.playerStatusWorker.myPlayerStatus.vesselText = FlightGlobals.ActiveVessel.vesselName;
                        SetInUse(FlightGlobals.ActiveVessel.id.ToString(), parent.settings.playerName);
                        parent.networkWorker.SendActiveVessel(FlightGlobals.ActiveVessel.id.ToString());
                        lastVessel = FlightGlobals.ActiveVessel.id.ToString();
                    }
                }
                else
                {
                    if (lastVessel != "")
                    {
                        //We are still in flight, 
                        lastVessel = "";
                        parent.networkWorker.SendActiveVessel("");
                        parent.playerStatusWorker.myPlayerStatus.vesselText = "";
                        SetNotInUse(parent.settings.playerName);
                    }
                }
            }
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                //Release the vessel if we aren't in flight anymore.
                if (lastVessel != "")
                {
                    lastVessel = "";
                    parent.networkWorker.SendActiveVessel("");
                    parent.playerStatusWorker.myPlayerStatus.vesselText = "";
                    SetNotInUse(parent.settings.playerName);
                }
            }
        }

        private void UpdatePackDistance(string vesselID)
        {
            foreach (Vessel v in FlightGlobals.fetch.vessels)
            {
                if (v.id.ToString() == vesselID)
                {
                    //Bump other players active vessels
                    if (inUse.ContainsKey(vesselID) ? (inUse[vesselID] != parent.settings.playerName) : false)
                    {
                        v.distanceLandedUnpackThreshold = PLAYER_UNPACK_THRESHOLD;
                        v.distanceLandedPackThreshold = PLAYER_PACK_THRESHOLD;
                        v.distanceUnpackThreshold = PLAYER_UNPACK_THRESHOLD;
                        v.distancePackThreshold = PLAYER_PACK_THRESHOLD;
                    }
                    else
                    {
                        v.distanceLandedUnpackThreshold = NORMAL_UNPACK_THRESHOLD;
                        v.distanceLandedPackThreshold = NORMAL_PACK_THRESHOLD;
                        v.distanceUnpackThreshold = NORMAL_UNPACK_THRESHOLD;
                        v.distancePackThreshold = NORMAL_PACK_THRESHOLD;
                    }
                }
            }
        }

        private void CheckVesselHasChanged()
        {
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.fetch.activeVessel != null)
            {
                if (!isSpectating && FlightGlobals.fetch.activeVessel.loaded && !FlightGlobals.fetch.activeVessel.packed)
                {
                    if (FlightGlobals.fetch.activeVessel.parts.Count != lastPartCount)
                    {
                        lastPartCount = FlightGlobals.fetch.activeVessel.parts.Count;
                        serverVesselsProtoUpdate[FlightGlobals.fetch.activeVessel.id.ToString()] = 0;
                    }
                }
            }
        }

        private void SendVesselUpdates()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                //We aren't in flight so we have nothing to send
                return;
            }
            if (FlightGlobals.ActiveVessel == null)
            {
                //We don't have an active vessel
                return;
            }
            if (!FlightGlobals.ActiveVessel.loaded || FlightGlobals.ActiveVessel.packed)
            {
                //We haven't loaded into the game yet
                return;
            }
            if (isSpectating)
            {
                //Don't send updates in spectate mode
                return;
            }
            if (isInSafetyBubble(FlightGlobals.fetch.activeVessel.GetWorldPos3D(), FlightGlobals.fetch.activeVessel.mainBody))
            {
                //Don't send updates while in the safety bubble
                return;
            }

            SendVesselUpdateIfNeeded(FlightGlobals.fetch.activeVessel, 0);
            SortedList<double, Vessel> secondryVessels = new SortedList<double, Vessel>();

            foreach (Vessel checkVessel in FlightGlobals.fetch.vessels)
            {
                //Only update the vessel if it's loaded and unpacked (not on rails). Skip our vessel.
                if (checkVessel.loaded && !checkVessel.packed && checkVessel.id.ToString() != FlightGlobals.fetch.activeVessel.id.ToString())
                {
                    //Don't update vessels in the safety bubble
                    if (!isInSafetyBubble(checkVessel.GetWorldPos3D(), checkVessel.mainBody))
                    {
                        //Don't update other players vessels
                        if (!inUse.ContainsKey(checkVessel.id.ToString()))
                        {
                            //Dont update vessels manipulated in the future
                            if (latestVesselUpdate.ContainsKey(checkVessel.id.ToString()) ? latestVesselUpdate[checkVessel.id.ToString()] < Planetarium.GetUniversalTime() : true)
                            {
                                double currentDistance = Vector3d.Distance(FlightGlobals.fetch.activeVessel.GetWorldPos3D(), checkVessel.GetWorldPos3D());
                                secondryVessels.Add(currentDistance, checkVessel);
                            }
                        }
                    }
                }
            }

            int currentSend = 0;
            foreach (KeyValuePair<double, Vessel> secondryVessel in secondryVessels)
            {
                currentSend++;
                if (currentSend > parent.dynamicTickWorker.maxSecondryVesselsPerTick)
                {
                    break;
                }
                SendVesselUpdateIfNeeded(secondryVessel.Value, secondryVessel.Key);
            }
        }

        private void SendVesselUpdateIfNeeded(Vessel checkVessel, double ourDistance)
        {
            //Check vessel parts
            if (!vesselPartsOk.ContainsKey(checkVessel.id.ToString()))
            {
                List<string> allowedParts = parent.modWorker.GetAllowedPartsList();
                List<string> bannedParts = new List<string>();
                foreach (ProtoPartSnapshot part in checkVessel.protoVessel.protoPartSnapshots)
                {
                    if (!allowedParts.Contains(part.partName))
                    {
                        if (!bannedParts.Contains(part.partName))
                        {
                            bannedParts.Add(part.partName);
                        }
                    }
                }
                if (checkVessel.id.ToString() == FlightGlobals.fetch.activeVessel.id.ToString())
                {
                    bannedPartsString = "";
                    foreach (string bannedPart in bannedParts)
                    {
                        bannedPartsString += bannedPart + "\n";
                    }
                }
                vesselPartsOk.Add(checkVessel.id.ToString(), (bannedParts.Count == 0));
            }
            if (!vesselPartsOk[checkVessel.id.ToString()])
            {
                if (checkVessel.id.ToString() == FlightGlobals.fetch.activeVessel.id.ToString())
                {
                    if ((UnityEngine.Time.realtimeSinceStartup - lastBannedPartsMessageUpdate) > UPDATE_SCREEN_MESSAGE_INTERVAL)
                    {
                        lastBannedPartsMessageUpdate = UnityEngine.Time.realtimeSinceStartup;
                        if (bannedPartsMessage != null)
                        {
                            bannedPartsMessage.duration = 0;
                        }
                        bannedPartsMessage = ScreenMessages.PostScreenMessage("Active vessel contains the following banned parts, it will not be saved to the server:\n" + bannedPartsString, 2f, ScreenMessageStyle.UPPER_CENTER);
                    }
                }
                //Vessel with bad parts
                return;
            }
            //Send updates for unpacked vessels that aren't being flown by other players
            bool notRecentlySentProtoUpdate = serverVesselsProtoUpdate.ContainsKey(checkVessel.id.ToString()) ? ((UnityEngine.Time.realtimeSinceStartup - serverVesselsProtoUpdate[checkVessel.id.ToString()]) > VESSEL_PROTOVESSEL_UPDATE_INTERVAL) : true;
            bool notRecentlySentPositionUpdate = serverVesselsPositionUpdate.ContainsKey(checkVessel.id.ToString()) ? ((UnityEngine.Time.realtimeSinceStartup - serverVesselsPositionUpdate[checkVessel.id.ToString()]) > (1f / (float)parent.dynamicTickWorker.sendTickRate)) : true;
            bool anotherPlayerCloser = false;
            //Skip checking player vessels, they are filtered out above in "oursOrNotInUse"
            if (checkVessel.id.ToString() != FlightGlobals.fetch.activeVessel.id.ToString())
            {
                foreach (KeyValuePair<string,string> entry in inUse)
                {
                    //The active vessel isn't another player that can be closer than the active vessel.
                    if (entry.Value != parent.settings.playerName)
                    {
                        Vessel playerVessel = FlightGlobals.fetch.vessels.FindLast(v => v.id.ToString() == entry.Key);
                        if (playerVessel != null)
                        {
                            double theirDistance = Vector3d.Distance(playerVessel.GetWorldPos3D(), checkVessel.GetWorldPos3D());
                            if (ourDistance > theirDistance)
                            {
                                //DarkLog.Debug("Player " + entry.Value + " is closer to " + entry.Key + ", theirs: " + theirDistance + ", ours: " + ourDistance);
                                anotherPlayerCloser = true;
                            }
                        }
                    }
                }
            }

            if (!anotherPlayerCloser)
            {
                //Check that is hasn't been recently sent
                if (notRecentlySentProtoUpdate)
                {
                    //Send a protovessel update
                    serverVesselsProtoUpdate[checkVessel.id.ToString()] = UnityEngine.Time.realtimeSinceStartup;
                    //Also delay the position send
                    serverVesselsPositionUpdate[checkVessel.id.ToString()] = UnityEngine.Time.realtimeSinceStartup;
                    ProtoVessel checkProto = new ProtoVessel(checkVessel);
                    if (checkProto != null)
                    {
                        if (checkProto.vesselID != Guid.Empty)
                        {
                            //Also check for kerbal state changes
                            foreach (ProtoPartSnapshot part in checkProto.protoPartSnapshots)
                            {
                                foreach (ProtoCrewMember pcm in part.protoModuleCrew)
                                {
                                    int kerbalID = HighLogic.CurrentGame.CrewRoster.IndexOf(pcm);
                                    if (!serverKerbals.ContainsKey(kerbalID))
                                    {
                                        //New kerbal
                                        DarkLog.Debug("Found new kerbal, sending...");
                                        serverKerbals[kerbalID] = new ProtoCrewMember(pcm);
                                        parent.networkWorker.SendKerbalProtoMessage(HighLogic.CurrentGame.CrewRoster.IndexOf(pcm), pcm);
                                    }
                                    else
                                    {
                                        bool kerbalDifferent = false;
                                        kerbalDifferent = (pcm.name != serverKerbals[kerbalID].name) || kerbalDifferent;
                                        kerbalDifferent = (pcm.courage != serverKerbals[kerbalID].courage) || kerbalDifferent;
                                        kerbalDifferent = (pcm.isBadass != serverKerbals[kerbalID].isBadass) || kerbalDifferent;
                                        kerbalDifferent = (pcm.rosterStatus != serverKerbals[kerbalID].rosterStatus) || kerbalDifferent;
                                        kerbalDifferent = (pcm.seatIdx != serverKerbals[kerbalID].seatIdx) || kerbalDifferent;
                                        kerbalDifferent = (pcm.stupidity != serverKerbals[kerbalID].stupidity) || kerbalDifferent;
                                        kerbalDifferent = (pcm.UTaR != serverKerbals[kerbalID].UTaR) || kerbalDifferent;
                                        if (kerbalDifferent)
                                        {
                                            DarkLog.Debug("Found changed kerbal, sending...");
                                            parent.networkWorker.SendKerbalProtoMessage(HighLogic.CurrentGame.CrewRoster.IndexOf(pcm), pcm);
                                            serverKerbals[kerbalID].name = pcm.name;
                                            serverKerbals[kerbalID].courage = pcm.courage;
                                            serverKerbals[kerbalID].isBadass = pcm.isBadass;
                                            serverKerbals[kerbalID].rosterStatus = pcm.rosterStatus;
                                            serverKerbals[kerbalID].seatIdx = pcm.seatIdx;
                                            serverKerbals[kerbalID].stupidity = pcm.stupidity;
                                            serverKerbals[kerbalID].UTaR = pcm.UTaR;
                                        }
                                    }
                                }
                            }
                            if (!serverVessels.Contains(checkProto.vesselID.ToString()))
                            {
                                serverVessels.Add(checkProto.vesselID.ToString());
                            }
                            parent.networkWorker.SendVesselProtoMessage(checkProto);
                        }
                        else
                        {
                            DarkLog.Debug(checkVessel.vesselName + " does not have a guid!");
                        }
                    }
                    else
                    {
                        DarkLog.Debug("Failed to send protovessel for " + checkVessel.id);
                    }
                }
                else if (notRecentlySentPositionUpdate)
                {
                    //Send a position update
                    serverVesselsPositionUpdate[checkVessel.id.ToString()] = UnityEngine.Time.realtimeSinceStartup;

                    if (!anotherPlayerCloser)
                    {
                        VesselUpdate update = GetVesselUpdate(checkVessel);
                        if (update != null)
                        {
                            parent.networkWorker.SendVesselUpdate(update);
                        }
                    }
                }
            }
        }
        //Also called from PlayerStatusWorker
        public bool isSpectating
        {
            get
            {
                if (FlightGlobals.fetch.activeVessel != null)
                {
                    if (inUse.ContainsKey(FlightGlobals.ActiveVessel.id.ToString()))
                    {
                        if (inUse[FlightGlobals.ActiveVessel.id.ToString()] != parent.settings.playerName)
                        {
                            spectateType = 1;
                            return true;
                        }
                    }
                    if (latestVesselUpdate.ContainsKey(FlightGlobals.fetch.activeVessel.id.ToString()))
                    {
                        if (latestVesselUpdate[FlightGlobals.fetch.activeVessel.id.ToString()] > Planetarium.GetUniversalTime())
                        {
                            spectateType = 2;
                            return true;
                        }
                    }
                }
                spectateType = 0;
                return false;
            }
        }
        //Adapted from KMP. Called from PlayerStatusWorker.
        public bool isInSafetyBubble(Vector3d worlPos, CelestialBody body)
        {
            //If not at Kerbin or past ceiling we're definitely clear
            if (body.name != "Kerbin")
            {
                return false;
            }
            Vector3d landingPadPosition = body.GetWorldSurfacePosition(-0.0971978130377757, 285.44237039111, 60);
            Vector3d runwayPosition = body.GetWorldSurfacePosition(-0.0486001121594686, 285.275552559723, 60);
            double landingPadDistance = Vector3d.Distance(worlPos, landingPadPosition);
            double runwayDistance = Vector3d.Distance(worlPos, runwayPosition);
            return runwayDistance < SAFETY_BUBBLE_DISTANCE || landingPadDistance < SAFETY_BUBBLE_DISTANCE;
        }
        //Adapted from KMP.
        private bool isProtoVesselInSafetyBubble(ProtoVessel protovessel)
        {
            //If not kerbin, we aren't in the safety bubble.
            if (protovessel.orbitSnapShot.ReferenceBodyIndex != FlightGlobals.Bodies.FindIndex(body => body.bodyName == "Kerbin"))
            {
                return false;
            }
            CelestialBody kerbinBody = FlightGlobals.Bodies.Find(b => b.name == "Kerbin");
            Vector3d protoVesselPosition = kerbinBody.GetWorldSurfacePosition(protovessel.latitude, protovessel.longitude, protovessel.altitude);
            return isInSafetyBubble(protoVesselPosition, kerbinBody);
        }

        private VesselUpdate GetVesselUpdate(Vessel updateVessel)
        {
            VesselUpdate returnUpdate = new VesselUpdate();
            try
            {
                returnUpdate.vesselID = updateVessel.id.ToString();
                returnUpdate.planetTime = Planetarium.GetUniversalTime();
                returnUpdate.bodyName = updateVessel.mainBody.bodyName;
                /*
                returnUpdate.rotation = new float[4];
                Quaternion transformRotation = updateVessel.transform.rotation;
                returnUpdate.rotation[0] = transformRotation.x;
                returnUpdate.rotation[1] = transformRotation.y;
                returnUpdate.rotation[2] = transformRotation.z;
                returnUpdate.rotation[3] = transformRotation.w;
                */
                returnUpdate.vesselForward = new float[3];
                Vector3 transformVesselForward = updateVessel.mainBody.transform.InverseTransformDirection(updateVessel.transform.forward);
                returnUpdate.vesselForward[0] = transformVesselForward.x;
                returnUpdate.vesselForward[1] = transformVesselForward.y;
                returnUpdate.vesselForward[2] = transformVesselForward.z;

                returnUpdate.vesselUp = new float[3];
                Vector3 transformVesselUp = updateVessel.mainBody.transform.InverseTransformDirection(updateVessel.transform.up);
                returnUpdate.vesselUp[0] = transformVesselUp.x;
                returnUpdate.vesselUp[1] = transformVesselUp.y;
                returnUpdate.vesselUp[2] = transformVesselUp.z;

                returnUpdate.angularVelocity = new float[3];
                returnUpdate.angularVelocity[0] = updateVessel.angularVelocity.x;
                returnUpdate.angularVelocity[1] = updateVessel.angularVelocity.y;
                returnUpdate.angularVelocity[2] = updateVessel.angularVelocity.z;
                //Flight state
                returnUpdate.flightState = new FlightCtrlState();
                returnUpdate.flightState.CopyFrom(updateVessel.ctrlState);
                returnUpdate.actiongroupControls = new bool[5];
                returnUpdate.actiongroupControls[0] = updateVessel.ActionGroups[KSPActionGroup.Gear];
                returnUpdate.actiongroupControls[1] = updateVessel.ActionGroups[KSPActionGroup.Light];
                returnUpdate.actiongroupControls[2] = updateVessel.ActionGroups[KSPActionGroup.Brakes];
                returnUpdate.actiongroupControls[3] = updateVessel.ActionGroups[KSPActionGroup.SAS];
                returnUpdate.actiongroupControls[4] = updateVessel.ActionGroups[KSPActionGroup.RCS];

                if (updateVessel.altitude < 10000)
                {
                    //Use surface position under 10k
                    returnUpdate.isSurfaceUpdate = true;
                    returnUpdate.position = new double[3];
                    returnUpdate.position[0] = updateVessel.latitude;
                    returnUpdate.position[1] = updateVessel.longitude;
                    returnUpdate.position[2] = updateVessel.altitude;
                    returnUpdate.velocity = new double[3];
                    returnUpdate.velocity[0] = updateVessel.srf_velocity.x;
                    returnUpdate.velocity[1] = updateVessel.srf_velocity.y;
                    returnUpdate.velocity[2] = updateVessel.srf_velocity.z;
                }
                else
                {
                    //Use orbital positioning over 10k
                    returnUpdate.isSurfaceUpdate = false;
                    returnUpdate.orbit = new double[7];
                    returnUpdate.orbit[0] = updateVessel.orbit.inclination;
                    returnUpdate.orbit[1] = updateVessel.orbit.eccentricity;
                    returnUpdate.orbit[2] = updateVessel.orbit.semiMajorAxis;
                    returnUpdate.orbit[3] = updateVessel.orbit.LAN;
                    returnUpdate.orbit[4] = updateVessel.orbit.argumentOfPeriapsis;
                    returnUpdate.orbit[5] = updateVessel.orbit.meanAnomalyAtEpoch;
                    returnUpdate.orbit[6] = updateVessel.orbit.epoch;
                }

            }
            catch (Exception e)
            {
                DarkLog.Debug("Failed to get vessel update, exception: " + e);
                returnUpdate = null;
            }
            return returnUpdate;
        }
        //Also called from network worker
        public void SetNotInUse(string player)
        {
            string deleteKey = "";
            foreach (KeyValuePair<string, string> inUseEntry in inUse)
            {
                if (inUseEntry.Value == player)
                {
                    deleteKey = inUseEntry.Key;
                }
            }
            if (deleteKey != "")
            {
                inUse.Remove(deleteKey);
            }
        }

        private void SetInUse(string vesselID, string player)
        {
            SetNotInUse(player);
            inUse[vesselID] = player;
        }
        //Called from main
        public void LoadKerbalsIntoGame()
        {
            foreach (KeyValuePair<int, Queue<KerbalEntry>> kerbalQueue in kerbalProtoQueue)
            {
                DarkLog.Debug("Loading " + kerbalQueue.Value.Count + " received kerbals from subspace " + kerbalQueue.Key);
                while (kerbalQueue.Value.Count > 0)
                {
                    KerbalEntry kerbalEntry = kerbalQueue.Value.Dequeue();
                    LoadKerbal(kerbalEntry.kerbalID, kerbalEntry.kerbalNode);
                }
            }

            int generateKerbals = 0;
            if (serverKerbals.Count < 50)
            {
                generateKerbals = 50 - serverKerbals.Count;
                DarkLog.Debug("Generating " + generateKerbals + " new kerbals");
            }

            while (generateKerbals > 0)
            {
                ProtoCrewMember protoKerbal = CrewGenerator.RandomCrewMemberPrototype();
                if (!HighLogic.CurrentGame.CrewRoster.ExistsInRoster(protoKerbal.name))
                {
                    HighLogic.CurrentGame.CrewRoster.AddCrewMember(protoKerbal);
                    int kerbalID = HighLogic.CurrentGame.CrewRoster.IndexOf(protoKerbal); 
                    serverKerbals[kerbalID] = new ProtoCrewMember(protoKerbal);
                    parent.networkWorker.SendKerbalProtoMessage(kerbalID, protoKerbal);
                    generateKerbals--;
                }
            }
        }

        private void LoadKerbal(int kerbalID, ConfigNode crewNode)
        {
            if (crewNode != null)
            {
                ProtoCrewMember protoCrew = new ProtoCrewMember(crewNode);
                if (protoCrew != null)
                {
                    if (!String.IsNullOrEmpty(protoCrew.name))
                    {
                        //Welcome to the world of kludges.
                        bool existsInRoster = true;
                        try
                        {
                            ProtoCrewMember testKerbal = HighLogic.CurrentGame.CrewRoster[kerbalID];
                        }
                        catch
                        {
                            existsInRoster = false;
                        }
                        if (!existsInRoster)
                        {
                            HighLogic.CurrentGame.CrewRoster.AddCrewMember(protoCrew);
                            DarkLog.Debug("Loaded kerbal " + kerbalID + ", name: " + protoCrew.name + ", state " + protoCrew.rosterStatus);
                            serverKerbals[kerbalID] = (new ProtoCrewMember(protoCrew));
                        }
                        else
                        {
                            HighLogic.CurrentGame.CrewRoster[kerbalID].name = protoCrew.name;
                            HighLogic.CurrentGame.CrewRoster[kerbalID].courage = protoCrew.courage;
                            HighLogic.CurrentGame.CrewRoster[kerbalID].isBadass = protoCrew.isBadass;
                            HighLogic.CurrentGame.CrewRoster[kerbalID].rosterStatus = protoCrew.rosterStatus;
                            HighLogic.CurrentGame.CrewRoster[kerbalID].seatIdx = protoCrew.seatIdx;
                            HighLogic.CurrentGame.CrewRoster[kerbalID].stupidity = protoCrew.stupidity;
                            HighLogic.CurrentGame.CrewRoster[kerbalID].UTaR = protoCrew.UTaR;
                        }
                    }
                    else
                    {
                        DarkLog.Debug("protoName is blank!");
                    }
                }
                else
                {
                    DarkLog.Debug("protoCrew is null!");
                }
            }
            else
            {
                DarkLog.Debug("crewNode is null!");
            }
        }
        //Called from main
        public void LoadVesselsIntoGame()
        {
            foreach (KeyValuePair<int, Queue<VesselProtoUpdate>> vesselQueue in vesselProtoQueue)
            {
                DarkLog.Debug("Loading " + vesselQueue.Value.Count + " vessels from subspace " + vesselQueue.Key);
                while (vesselQueue.Value.Count > 0)
                {
                    ConfigNode currentNode = vesselQueue.Value.Dequeue().vesselNode;
                    LoadVessel(currentNode);
                }
            }
        }
        //Thanks KMP :)
        private void checkProtoNodeCrew(ref ConfigNode protoNode)
        {
            string protoVesselID = protoNode.GetValue("pid");
            foreach (ConfigNode partNode in protoNode.GetNodes("PART"))
            {
                int currentCrewIndex = 0;
                foreach (string crew in partNode.GetValues("crew"))
                {
                    int crewValue = Convert.ToInt32(crew);
                    DarkLog.Debug("Protovessel: " + protoVesselID + " crew value " + crewValue);
                    if (assignedKerbals.ContainsKey(crewValue) ? assignedKerbals[crewValue] != protoVesselID : false)
                    {
                        DarkLog.Debug("Kerbal taken!");
                        if (assignedKerbals[crewValue] != protoVesselID)
                        {
                            //Assign a new kerbal, this one already belongs to another ship.
                            int freeKerbal = 0;
                            while (assignedKerbals.ContainsKey(freeKerbal))
                            {
                                freeKerbal++;
                            }
                            partNode.SetValue("crew", freeKerbal.ToString(), currentCrewIndex);
                            CheckCrewMemberExists(freeKerbal);
                            HighLogic.CurrentGame.CrewRoster[freeKerbal].rosterStatus = ProtoCrewMember.RosterStatus.ASSIGNED;
                            HighLogic.CurrentGame.CrewRoster[freeKerbal].seatIdx = currentCrewIndex;
                            DarkLog.Debug("Fixing duplicate kerbal reference, changing kerbal " + currentCrewIndex + " to " + freeKerbal);
                            crewValue = freeKerbal;
                            assignedKerbals[crewValue] = protoVesselID;
                            currentCrewIndex++;
                        }
                    }
                    else
                    {
                        assignedKerbals[crewValue] = protoVesselID;
                        CheckCrewMemberExists(crewValue);
                        HighLogic.CurrentGame.CrewRoster[crewValue].rosterStatus = ProtoCrewMember.RosterStatus.ASSIGNED;
                        HighLogic.CurrentGame.CrewRoster[crewValue].seatIdx = currentCrewIndex;
                    }
                    crewValue++;
                }
            }   
        }
        //Again - KMP :)
        private void CheckCrewMemberExists(int kerbalID)
        {
            IEnumerator<ProtoCrewMember> crewEnum = HighLogic.CurrentGame.CrewRoster.GetEnumerator();
            int currentKerbals = 0;
            while (crewEnum.MoveNext())
            {
                currentKerbals++;
            }
            if (currentKerbals <= kerbalID)
            {
                DarkLog.Debug("Generating " + ((kerbalID + 1) - currentKerbals) + " new kerbal for an assigned crew index " + kerbalID);
            }
            while (currentKerbals <= kerbalID)
            {
                ProtoCrewMember protoKerbal = CrewGenerator.RandomCrewMemberPrototype();
                if (!HighLogic.CurrentGame.CrewRoster.ExistsInRoster(protoKerbal.name))
                {
                    DarkLog.Debug("Generated new kerbal " + protoKerbal.name + ", ID: " + currentKerbals);
                    HighLogic.CurrentGame.CrewRoster.AddCrewMember(protoKerbal);
                    int newKerbalID = HighLogic.CurrentGame.CrewRoster.IndexOf(protoKerbal); 
                    serverKerbals[newKerbalID] = new ProtoCrewMember(protoKerbal);
                    parent.networkWorker.SendKerbalProtoMessage(newKerbalID, protoKerbal);
                    currentKerbals++;
                }
            }
        }
        //Also called from QuickSaveLoader
        public void LoadVessel(ConfigNode vesselNode)
        {
            if (vesselNode != null)
            {
                //Fix the kerbals (Tracking station bug)
                checkProtoNodeCrew(ref vesselNode);

                //Can be used for debugging incoming vessel config nodes.
                //vesselNode.Save(Path.Combine(KSPUtil.ApplicationRootPath, Path.Combine("DMP-RX", Planetarium.GetUniversalTime() + ".txt")));
                ProtoVessel currentProto = new ProtoVessel(vesselNode, HighLogic.CurrentGame);
                if (currentProto != null)
                {
                    if (isProtoVesselInSafetyBubble(currentProto))
                    {
                        DarkLog.Debug("Removing protovessel " + currentProto.vesselID.ToString() + ", name: " + currentProto.vesselName + " from server - In safety bubble!");
                        parent.networkWorker.SendVesselRemove(currentProto.vesselID.ToString());
                        return;
                    }
                    if (!serverVessels.Contains(currentProto.vesselID.ToString()))
                    {
                        serverVessels.Add(currentProto.vesselID.ToString());
                    }
                    DarkLog.Debug("Loading " + currentProto.vesselID + ", name: " + currentProto.vesselName + ", type: " + currentProto.vesselType);

                    foreach (ProtoPartSnapshot part in currentProto.protoPartSnapshots)
                    {
                        //This line doesn't actually do anything useful, but if you get this reference, you're officially the most geeky person darklight knows.
                        part.temperature = ((part.temperature + 273.15f) * 0.8f) - 273.15f;
                    }

                    bool wasActive = false;
                    bool wasTarget = false;

                    if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                    {
                        if (FlightGlobals.fetch.VesselTarget != null ? FlightGlobals.fetch.VesselTarget.GetVessel() != null : false)
                        {
                            wasTarget = FlightGlobals.fetch.VesselTarget.GetVessel().id == currentProto.vesselID;
                        }
                        if (wasTarget)
                        {
                            DarkLog.Debug("ProtoVessel update for target vessel!");
                        }
                        wasActive = (FlightGlobals.ActiveVessel != null) ? (FlightGlobals.ActiveVessel.id == currentProto.vesselID) : false;
                        if (wasActive)
                        {
                            DarkLog.Debug("ProtoVessel update for active vessel!");
                            try
                            {
                                OrbitPhysicsManager.HoldVesselUnpack(5);
                            }
                            catch
                            {
                                //Don't care.
                            }
                            FlightGlobals.fetch.activeVessel.MakeInactive();
                        }
                    }

                    //Kill old vessels, temporarily turning off destruction notifications to the server.
                    bool oldDestroyIsValid = destroyIsValid;
                    destroyIsValid = false;
                    for (int vesselID = FlightGlobals.fetch.vessels.Count - 1; vesselID >= 0; vesselID--)
                    {
                        Vessel oldVessel = FlightGlobals.fetch.vessels[vesselID];
                        if (oldVessel.id.ToString() == currentProto.vesselID.ToString())
                        {
                            KillVessel(oldVessel);
                        }
                    }
                    destroyIsValid = oldDestroyIsValid;

                    //This has to be the most ugliest peice of code you will ever see.
                    //It spawns ships around the sun if the vessel is close. I feel bad for this, but the atmosphere is poison.
                    if (currentProto.situation == Vessel.Situations.FLYING)
                    {
                        if (FlightGlobals.fetch.activeVessel != null)
                        {
                            if ((FlightGlobals.Bodies.IndexOf(FlightGlobals.fetch.activeVessel.mainBody) == currentProto.orbitSnapShot.ReferenceBodyIndex) && (currentProto.altitude < FlightGlobals.fetch.activeVessel.mainBody.maxAtmosphereAltitude))
                            {
                                double distance = Vector3d.Distance(FlightGlobals.fetch.activeVessel.GetWorldPos3D(), FlightGlobals.fetch.activeVessel.mainBody.GetWorldSurfacePosition(currentProto.latitude, currentProto.longitude, currentProto.altitude));
                                DarkLog.Debug("Using sun loading hack for " + currentProto.vesselID.ToString() + ", distance: " + distance);
                                if (distance < 2500)
                                {
                                    //Put it 10km above the atmosphere.
                                    double hackDistance = 10000 + FlightGlobals.fetch.activeVessel.mainBody.maxAtmosphereAltitude + FlightGlobals.fetch.activeVessel.mainBody.Radius;
                                    OrbitSnapshot os = new OrbitSnapshot(new Orbit(0, 0, hackDistance, 0, 0, 0, 0, FlightGlobals.fetch.activeVessel.mainBody));
                                    currentProto.orbitSnapShot = os;
                                }
                            }
                        }
                    }

                    serverVesselsProtoUpdate[currentProto.vesselID.ToString()] = UnityEngine.Time.realtimeSinceStartup;
                    //Spawn the vessel into the game
                    currentProto.Load(HighLogic.CurrentGame.flightState);

                    if (currentProto.vesselRef != null)
                    {
                        UpdatePackDistance(currentProto.vesselRef.id.ToString());

                        if (wasActive)
                        {
                            switchActiveVesselOnNextUpdate = currentProto.vesselRef;
                        }
                        if (wasTarget)
                        {
                            DarkLog.Debug("Set docking target");
                            FlightGlobals.fetch.SetVesselTarget(currentProto.vesselRef);
                        }
                        DarkLog.Debug("Protovessel Loaded");
                    }
                    else
                    {
                        DarkLog.Debug("Protovessel " + currentProto.vesselID + " failed to create a vessel!");
                    }
                }
                else
                {
                    DarkLog.Debug("protoVessel is null!");
                }
            }
            else
            {
                DarkLog.Debug("vesselNode is null!");
            }
        }

        public void OnVesselDestroyed(Vessel dyingVessel)
        {
            if (destroyIsValid)
            {
                if (latestVesselUpdate.ContainsKey(dyingVessel.id.ToString()) ? latestVesselUpdate[dyingVessel.id.ToString()] < Planetarium.GetUniversalTime() : true)
                {
                    //Remove the vessel from the server if it's not owned by another player.
                    if ((inUse.ContainsKey(dyingVessel.id.ToString()) ? inUse[dyingVessel.id.ToString()] == parent.settings.playerName : true))
                    {
                        if (serverVessels.Contains(dyingVessel.id.ToString()))
                        {
                            DarkLog.Debug("Removing vessel " + dyingVessel.id.ToString() + ", name: " + dyingVessel.vesselName + " from the server: Destroyed");
                            unassignKerbals(dyingVessel.id.ToString());
                            serverVessels.Remove(dyingVessel.id.ToString());
                        }
                    }
                    else
                    {
                        DarkLog.Debug("Skipping the removal of vessel " + dyingVessel.id.ToString() + ", name: " + dyingVessel.vesselName + ", owned by another player.");
                    }
                }
                else
                {
                    DarkLog.Debug("Skipping the removal of vessel " + dyingVessel.id.ToString() + ", name: " + dyingVessel.vesselName + ", vessel has been changed in the future.");
                }
            }
            else
            {
                DarkLog.Debug("Skipping the removal of vessel " + dyingVessel.id.ToString() + ", name: " + dyingVessel.vesselName + ", destructions are not valid.");
            }
        }

        public void OnVesselRecovered(ProtoVessel recoveredVessel)
        {
            //Check the vessel hasn't been changed in the past
            if (latestVesselUpdate.ContainsKey(recoveredVessel.vesselID.ToString()) ? latestVesselUpdate[recoveredVessel.vesselID.ToString()] < Planetarium.GetUniversalTime() : true)
            {
                //Remove the vessel from the server if it's not owned by another player.
                if (inUse.ContainsKey(recoveredVessel.vesselID.ToString()) ? inUse[recoveredVessel.vesselID.ToString()] == parent.settings.playerName : true)
                {
                    //Check that it's a server vessel
                    if (serverVessels.Contains(recoveredVessel.vesselID.ToString()))
                    {
                        DarkLog.Debug("Removing vessel " + recoveredVessel.vesselID.ToString() + ", name: " + recoveredVessel.vesselName + " from the server: Recovered");
                        unassignKerbals(recoveredVessel.vesselID.ToString());
                        serverVessels.Remove(recoveredVessel.vesselID.ToString());
                    }
                }
            }
            else
            {
                ScreenMessages.PostScreenMessage("Cannot recover vessel, the vessel been changed in the future.", 5f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        public void OnVesselTerminated(ProtoVessel terminatedVessel)
        {
            //Check the vessel hasn't been changed in the past
            if (latestVesselUpdate.ContainsKey(terminatedVessel.vesselID.ToString()) ? latestVesselUpdate[terminatedVessel.vesselID.ToString()] < Planetarium.GetUniversalTime() : true)
            {
                //Remove the vessel from the server if it's not owned by another player.
                if (inUse.ContainsKey(terminatedVessel.vesselID.ToString()) ? inUse[terminatedVessel.vesselID.ToString()] == parent.settings.playerName : true)
                {
                    if (serverVessels.Contains(terminatedVessel.vesselID.ToString()))
                    {
                        DarkLog.Debug("Removing vessel " + terminatedVessel.vesselID.ToString() + ", name: " + terminatedVessel.vesselName + " from the server: Terminated");
                        unassignKerbals(terminatedVessel.vesselID.ToString());
                        serverVessels.Remove(terminatedVessel.vesselID.ToString());
                    }
                }
            }
            else
            {
                ScreenMessages.PostScreenMessage("Cannot terminate vessel, the vessel been changed in the future.", 5f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        public void OnGameSceneLoadRequested(GameScenes scene)
        {
            if (destroyIsValid)
            {
                DarkLog.Debug("Vessel destructions are now invalid");
                destroyIsValid = false;
            }
        }

        public void OnFlightReady()
        {
            if (!destroyIsValid)
            {
                DarkLog.Debug("Vessel destructions are now valid");
                destroyIsValid = true;
            }
        }

        private void unassignKerbals(string vesselID)
        {
            List<int> unassignKerbals = new List<int>();
            foreach (KeyValuePair<int,string> kerbalAssignment in assignedKerbals)
            {
                if (kerbalAssignment.Value == vesselID.Replace("-", ""))
                {
                    DarkLog.Debug("Kerbal " + kerbalAssignment.Key + " unassigned from " + vesselID);
                    unassignKerbals.Add(kerbalAssignment.Key);
                }
            }
            foreach (int unassignKerbal in unassignKerbals)
            {
                assignedKerbals.Remove(unassignKerbal);
                if (!isSpectating)
                {
                    parent.networkWorker.SendKerbalProtoMessage(unassignKerbal, HighLogic.CurrentGame.CrewRoster[unassignKerbal]);
                }
            }
        }

        private void KillVessel(Vessel killVessel)
        {
            if (killVessel != null)
            {
                DarkLog.Debug("Killing vessel: " + killVessel.id.ToString());
                if (!killVessel.packed)
                {
                    killVessel.GoOnRails();
                }
                if (killVessel.loaded)
                {
                    killVessel.Unload();
                }
                killVessel.Die();
            }
        }

        private void RemoveVessel(string vesselID)
        {
            for (int i = FlightGlobals.fetch.vessels.Count - 1; i >= 0; i--)
            {
                Vessel checkVessel = FlightGlobals.fetch.vessels[i];
                if (checkVessel.id.ToString() == vesselID)
                {
                    if (FlightGlobals.fetch.activeVessel != null ? FlightGlobals.fetch.activeVessel.id.ToString() == checkVessel.id.ToString() : false)
                    {
                        if (!isSpectating)
                        {
                            //Got a remove message for our vessel, reset the send time on our vessel so we send it back.
                            DarkLog.Debug("Resending vessel, our vessel was removed by another player");
                            serverVesselsProtoUpdate[vesselID] = 0f;
                        }
                    }
                    else
                    {
                        DarkLog.Debug("Removing vessel: " + vesselID);
                        KillVessel(checkVessel);
                    }
                }
            }
        }

        private void ApplyVesselUpdate(VesselUpdate update)
        {
            if (HighLogic.LoadedScene == GameScenes.LOADING)
            {
                return;
            }
            //Get updating player
            string updatePlayer = inUse.ContainsKey(update.vesselID) ? inUse[update.vesselID] : "Unknown";
            //Ignore updates to our own vessel
            if (!isSpectating && (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.id.ToString() == update.vesselID : false))
            {
                DarkLog.Debug("ApplyVesselUpdate - Ignoring update for active vessel from " + updatePlayer);
                return;
            }
            Vessel updateVessel = FlightGlobals.fetch.vessels.FindLast(v => v.id.ToString() == update.vesselID);
            if (updateVessel == null)
            {
                //DarkLog.Debug("ApplyVesselUpdate - Got vessel update for " + update.vesselID + " but vessel does not exist");
                return;
            }
            CelestialBody updateBody = FlightGlobals.Bodies.Find(b => b.bodyName == update.bodyName);
            if (updateBody == null)
            {
                DarkLog.Debug("ApplyVesselUpdate - updateBody not found");
                return;
            }
            if (update.isSurfaceUpdate)
            {
                double updateDistance = Double.PositiveInfinity;
                if ((HighLogic.LoadedScene == GameScenes.FLIGHT) && (FlightGlobals.fetch.activeVessel != null))
                {
                    updateDistance = Vector3.Distance(FlightGlobals.fetch.activeVessel.GetWorldPos3D(), updateVessel.GetWorldPos3D());
                }
                bool isUnpacking = (updateDistance < updateVessel.distanceUnpackThreshold) && updateVessel.packed;
                if (!updateVessel.packed || (updateVessel.packed && !isUnpacking))
                {
                    Vector3d updatePostion = updateBody.GetWorldSurfacePosition(update.position[0], update.position[1], update.position[2]);
                    Vector3d updateVelocity = new Vector3d(update.velocity[0], update.velocity[1], update.velocity[2]);
                    Vector3d velocityOffset = updateVelocity - updateVessel.srf_velocity;
                    updateVessel.SetPosition(updatePostion);
                    updateVessel.ChangeWorldVelocity(velocityOffset);
                }
            }
            else
            {
                Orbit updateOrbit = new Orbit(update.orbit[0], update.orbit[1], update.orbit[2], update.orbit[3], update.orbit[4], update.orbit[5], update.orbit[6], updateBody);

                if (updateVessel.packed)
                {
                    CopyOrbit(updateOrbit, updateVessel.orbitDriver.orbit);
                }
                else
                {
                    updateVessel.SetPosition(updateOrbit.getPositionAtUT(Planetarium.GetUniversalTime()));
                    Vector3d velocityOffset = updateOrbit.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()).xzy - updateVessel.orbit.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()).xzy;
                    updateVessel.ChangeWorldVelocity(velocityOffset);
                }

            }
            //Quaternion updateRotation = new Quaternion(update.rotation[0], update.rotation[1], update.rotation[2], update.rotation[3]);
            //updateVessel.SetRotation(updateRotation);

            Vector3 vesselForward = new Vector3(update.vesselForward[0], update.vesselForward[1], update.vesselForward[2]);
            Vector3 vesselUp = new Vector3(update.vesselUp[0], update.vesselUp[1], update.vesselUp[2]);

            updateVessel.transform.LookAt(updateVessel.transform.position + updateVessel.mainBody.transform.TransformDirection(vesselForward).normalized, updateVessel.mainBody.transform.TransformDirection(vesselUp));
            updateVessel.SetRotation(updateVessel.transform.rotation);

            if (!updateVessel.packed)
            {
                updateVessel.angularVelocity = new Vector3(update.angularVelocity[0], update.angularVelocity[1], update.angularVelocity[2]);
            }
            if (!isSpectating)
            {
                updateVessel.ctrlState.CopyFrom(update.flightState);
            }
            else
            {
                FlightInputHandler.state.CopyFrom(update.flightState);
            }
            updateVessel.ActionGroups.SetGroup(KSPActionGroup.Gear, update.actiongroupControls[0]);
            updateVessel.ActionGroups.SetGroup(KSPActionGroup.Light, update.actiongroupControls[1]);
            updateVessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, update.actiongroupControls[2]);
            updateVessel.ActionGroups.SetGroup(KSPActionGroup.SAS, update.actiongroupControls[3]);
            updateVessel.ActionGroups.SetGroup(KSPActionGroup.RCS, update.actiongroupControls[4]);
        }
        //Credit where credit is due, Thanks hyperedit.
        private void CopyOrbit(Orbit sourceOrbit, Orbit destinationOrbit)
        {
            destinationOrbit.inclination = sourceOrbit.inclination;
            destinationOrbit.eccentricity = sourceOrbit.eccentricity;
            destinationOrbit.semiMajorAxis = sourceOrbit.semiMajorAxis;
            destinationOrbit.LAN = sourceOrbit.LAN;
            destinationOrbit.argumentOfPeriapsis = sourceOrbit.argumentOfPeriapsis;
            destinationOrbit.meanAnomalyAtEpoch = sourceOrbit.meanAnomalyAtEpoch;
            destinationOrbit.epoch = sourceOrbit.epoch;
            destinationOrbit.referenceBody = sourceOrbit.referenceBody;
            destinationOrbit.Init();
            destinationOrbit.UpdateFromUT(Planetarium.GetUniversalTime());
        }
        //Called from networkWorker
        public void QueueKerbal(int subspace, double planetTime, int kerbalID, ConfigNode kerbalNode)
        {
            KerbalEntry newEntry = new KerbalEntry();
            newEntry.kerbalID = kerbalID;
            newEntry.planetTime = planetTime;
            newEntry.kerbalNode = kerbalNode;
            if (!vesselRemoveQueue.ContainsKey(subspace))
            {
                SetupSubspace(subspace);
            }
            kerbalProtoQueue[subspace].Enqueue(newEntry);
        }
        //Called from networkWorker
        public void QueueVesselRemove(int subspace, double planetTime, string vesselID)
        {
            if (!vesselRemoveQueue.ContainsKey(subspace))
            {
                SetupSubspace(subspace);
            }
            VesselRemoveEntry vre = new VesselRemoveEntry();
            vre.planetTime = planetTime;
            vre.vesselID = vesselID;
            vesselRemoveQueue[subspace].Enqueue(vre);
            if (latestVesselUpdate.ContainsKey(vesselID) ? latestVesselUpdate[vesselID] < planetTime : true)
            {
                latestVesselUpdate[vesselID] = planetTime;
            }
        }

        public void QueueVesselProto(int subspace, double planetTime, ConfigNode vesselNode)
        {
            if (!vesselProtoQueue.ContainsKey(subspace))
            {
                SetupSubspace(subspace);
            }
            VesselProtoUpdate vpu = new VesselProtoUpdate();
            vpu.planetTime = planetTime;
            vpu.vesselNode = vesselNode;
            vesselProtoQueue[subspace].Enqueue(vpu);
        }

        public void QueueVesselUpdate(int subspace, VesselUpdate update)
        {
            if (!vesselUpdateQueue.ContainsKey(subspace))
            {
                SetupSubspace(subspace);
            }
            vesselUpdateQueue[subspace].Enqueue(update);
            if (latestVesselUpdate.ContainsKey(update.vesselID) ? latestVesselUpdate[update.vesselID] < update.planetTime : true)
            {
                latestVesselUpdate[update.vesselID] = update.planetTime;
            }
        }

        public void QueueActiveVessel(string player, string vesselID)
        {
            ActiveVesselEntry ave = new ActiveVesselEntry();
            ave.player = player;
            ave.vesselID = vesselID;
            newActiveVessels.Enqueue(ave);
        }

        private void SetupSubspace(int subspaceID)
        {
            lock (createSubspaceLock)
            {
                kerbalProtoQueue.Add(subspaceID, new Queue<KerbalEntry>());
                vesselRemoveQueue.Add(subspaceID, new Queue<VesselRemoveEntry>());
                vesselProtoQueue.Add(subspaceID, new Queue<VesselProtoUpdate>());
                vesselUpdateQueue.Add(subspaceID, new Queue<VesselUpdate>());
            }
        }
        //Called from main
        public void Reset()
        {
            workerEnabled = false;
            newActiveVessels = new Queue<ActiveVesselEntry>();
            kerbalProtoQueue = new Dictionary<int, Queue<KerbalEntry>>();
            vesselRemoveQueue = new Dictionary<int,Queue<VesselRemoveEntry>>();
            vesselProtoQueue = new Dictionary<int, Queue<VesselProtoUpdate>>();
            vesselUpdateQueue = new Dictionary<int, Queue<VesselUpdate>>();
            serverKerbals = new Dictionary<int, ProtoCrewMember>();
            assignedKerbals = new Dictionary<int, string>();
            serverVesselsProtoUpdate = new Dictionary<string, float>();
            serverVesselsPositionUpdate = new Dictionary<string, float>();
            latestVesselUpdate = new Dictionary<string, double>();
            serverVessels = new List<string>();
            vesselPartsOk = new Dictionary<string, bool>();
            inUse = new Dictionary<string, string>();
            lastVessel = "";
            gameSceneChangeTime = 0f;
            spectateType = 0;
            destroyIsValid = false;
        }

        public int GetStatistics(string statType)
        {
            switch (statType)
            {
                case "StoredFutureUpdates":
                    {
                        int futureUpdates = 0;
                        foreach (KeyValuePair<int, Queue<VesselUpdate>> vUQ in vesselUpdateQueue)
                        {
                            futureUpdates += vUQ.Value.Count;
                        }
                        return futureUpdates;
                    }
                case "StoredFutureProtoUpdates":
                    {
                        int futureProtoUpdates = 0;
                        foreach (KeyValuePair<int, Queue<VesselProtoUpdate>> vPQ in vesselProtoQueue)
                        {
                            futureProtoUpdates += vPQ.Value.Count;
                        }
                        return futureProtoUpdates;
                    }
            }
            return 0;
        }
    }

    class ActiveVesselEntry
    {
        public string player;
        public string vesselID;
    }

    class VesselRemoveEntry
    {
        public string vesselID;
        public double planetTime;
    }

    class VesselProtoUpdate
    {
        public double planetTime;
        public ConfigNode vesselNode;
    }

    class KerbalEntry
    {
        public int kerbalID;
        public double planetTime;
        public ConfigNode kerbalNode;
    }

    public class VesselUpdate
    {
        public string vesselID;
        public double planetTime;
        public string bodyName;
        //public float[] rotation;
        public float[] vesselForward;
        public float[] vesselUp;
        public float[] angularVelocity;
        public FlightCtrlState flightState;
        public bool[] actiongroupControls;
        public bool isSurfaceUpdate;
        //Orbital parameters
        public double[] orbit;
        //Surface parameters
        //Position = lat,long,alt.
        public double[] position;
        public double[] velocity;
    }
}

