using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

using UnityEngine;

namespace KLF
{
    public class KLFManager : MonoBehaviour
    {
        public struct VesselEntry
        {
            public KLFVessel Vessel;
            public float LastUpdateTime;
        }
        public struct VesselStatusInfo
        {
            public string User;
            public string VesselName;
            public string DetailText;
            public Color Color;
            public KLFVesselInfo Info;
            public Orbit Orbit;
            public float LastUpdateTime;
        }

        //Singleton
        public static GameObject GameObjectInstance;

        //Properties
        public const String InteropClientFilename = "interopclient.txt";
        public const String InteropPluginFilename = "interopplugin.txt";
        public const String GlobalSettingsFilename = "globalsettings.txt";

        public const float InactiveVesselRange = 400000000.0f;
        public const float DockingTargetRange = 200.0f;
        public const int MaxInactiveVesselsPerUpdate = 8;
        public const int StatusArrayMinSize = 2;
        public const int MaxVesselNameLength = 32;
        public const float VesselTimeoutDelay = 6.0f;
        public const float IdleDelay = 120.0f;
        public const float PluginDataWriteInterval = 5.0f;
        public const float GlobalSettingsSaveInterval = 10.0f;

        public const int InteropMaxQueueSize = 128;
        public const float InteropWriteInterval = 0.1f;
        public const float InteropWriteTimeout = 6.0f;

        public UnicodeEncoding Encoder = new UnicodeEncoding();

        public String PlayerName = String.Empty;
        public byte PerUpdate = 0;
        public float UpdateInterval = 0.25f;

        public Dictionary<String, VesselEntry> Vessels = new Dictionary<string, VesselEntry>();
        public SortedDictionary<String, VesselStatusInfo> PlayerStatus = new SortedDictionary<string, VesselStatusInfo>();
        public RenderingManager RenderManager;
        public PlanetariumCamera PlanetariumCam;

        public Queue<byte[]> InteropOutQueue = new Queue<byte[]>();

        private float LastGlobalSettingSaveTime = 0.0f;
        private float LastPluginDataWriteTime = 0.0f;
        private float LastPluginUpdateWriteTime = 0.0f;
        private float LastInteropWriteTime = 0.0f;
        private float LastKeyPressTime = 0.0f;

        private Queue<KLFVesselUpdate> VesselUpdateQueue = new Queue<KLFVesselUpdate>();

        GUIStyle PlayerNameStyle, VesselNameStyle, StateTextStyle, ChatLineStyle, ScreenshotDescriptionStyle;

        private bool MappingGUIToggleKey = false;
        private bool MappingScreenshotKey = false;
        private bool MappingChatKey = false;
        private bool MappingViewKey = false;
        private bool SharingScreenshot = false;

        public bool GlobalUIToggle
        {
            get
            {
                return RenderManager == null || RenderManager.uiElementsToDisable.Length < 1 || RenderManager.uiElementsToDisable[0].activeSelf;
            }
        }

        public bool SceneIsValid
        {
            get
            {
                switch (HighLogic.LoadedScene)
                {
                    case GameScenes.SPACECENTER:
                    case GameScenes.EDITOR:
                    case GameScenes.FLIGHT:
                    case GameScenes.SPH:
                    case GameScenes.TRACKSTATION:
                        return true;
                    default:
                        return false;
                }
            }
        }
        public bool ShouldDrawGui
        {
            get
            {
                return SceneIsValid && KLFInfoDisplay.InfoDisplayActive && GlobalUIToggle;
            }
        }
        public static bool IsInFlight
        {
            get
            {
                return FlightGlobals.ready && FlightGlobals.ActiveVessel != null;
            }
        }
        public bool IsIdle
        {
            get
            {
                return LastKeyPressTime > 0.0f && (UnityEngine.Time.realtimeSinceStartup - LastKeyPressTime) > IdleDelay;
            }
        }

        //Keys
        public bool GetAnyKeyDown(ref KeyCode key)
        {
            foreach (KeyCode keycode in Enum.GetValues(typeof(KeyCode)))
                if (Input.GetKeyDown(keycode))
                {
                    key = keycode;
                    return true;
                }
            return false;
        }

        //Updates
        public void UpdateStep()
        {
            //Don't do anything while the game is loading
            if (HighLogic.LoadedScene == GameScenes.LOADING)
                return;

            if (PlanetariumCam != null && PlanetariumCam.gameObject.GetComponent<KLFCameraScript>() == null)
            {
                Debug.Log("Added KLF Camera Script");
                KLFCameraScript script = PlanetariumCam.gameObject.AddComponent<KLFCameraScript>();
                script.Manager = this;
            }

            //Handle all queued vessel updates
            while (VesselUpdateQueue.Count > 0)
                HandleVesselUpdate(VesselUpdateQueue.Dequeue());
            //read from relay client
            ReadClientInterop();
            //write to relay client
            //  Plugin Update
            if((UnityEngine.Time.realtimeSinceStartup - LastPluginUpdateWriteTime) > UpdateInterval
            && (Time.realtimeSinceStartup - LastInteropWriteTime) < InteropWriteTimeout)
                WritePluginUpdate();

            //  Plugin Data
            if ((UnityEngine.Time.realtimeSinceStartup - LastPluginDataWriteTime) > PluginDataWriteInterval)
            {
                WritePluginData();
                WriteScreenshotWatchUpdate();
                LastPluginDataWriteTime = UnityEngine.Time.realtimeSinceStartup;
            }

            //  Write interop
            if ((UnityEngine.Time.realtimeSinceStartup - LastInteropWriteTime) > InteropWriteInterval)
                if (WritePluginInterop())
                    LastInteropWriteTime = UnityEngine.Time.realtimeSinceStartup;

            //Save global settings periodically
            if ((UnityEngine.Time.realtimeSinceStartup - LastGlobalSettingSaveTime) > GlobalSettingsSaveInterval)
                SaveGlobalSettings();

            //Update the positions of all the vessels
            List<String> deleteList = new List<String>();
            foreach (KeyValuePair<String, VesselEntry> pair in Vessels)
            {
                VesselEntry entry = pair.Value;
                if ((UnityEngine.Time.realtimeSinceStartup - entry.LastUpdateTime) <= VesselTimeoutDelay
                && entry.Vessel != null && entry.Vessel.GameObj != null)
                {
                    entry.Vessel.UpdateRenderProperties(
                        !KLFGlobalSettings.Instance.ShowOtherShips
                        || (!KLFGlobalSettings.Instance.ShowInactiveShips
                            && entry.Vessel.Info.State != State.Active)
                    );
                    entry.Vessel.UpdatePosition();
                }
                else
                {
                    deleteList.Add(pair.Key); //Mark the vessel for deletion
                    if (entry.Vessel != null && entry.Vessel.GameObj != null)
                        GameObject.Destroy(entry.Vessel.GameObj);
                }
            }

            //Delete what needs deletin'
            foreach (String key in deleteList)
                Vessels.Remove(key);
            deleteList.Clear();

            //Delete outdated player status entries
            foreach (KeyValuePair<String, VesselStatusInfo> pair in PlayerStatus)
            {
                if ((UnityEngine.Time.realtimeSinceStartup - pair.Value.LastUpdateTime) > VesselTimeoutDelay)
                    deleteList.Add(pair.Key);
            }

            foreach (String key in deleteList)
                PlayerStatus.Remove(key);
        }

        private void WritePluginUpdate()
        {
            if (PlayerName == null || PlayerName.Length == 0)
                return;
            WritePrimaryUpdate();
            if (IsInFlight)
                WriteSecondaryUpdates();
            LastPluginUpdateWriteTime = UnityEngine.Time.realtimeSinceStartup;
        }

        private void WritePrimaryUpdate()
        {
            if (IsInFlight)
            {
                //Write vessel status
                KLFVesselUpdate update = GetVesselUpdate(FlightGlobals.ActiveVessel);
                //Update the player vessel info
                VesselStatusInfo myStatus = new VesselStatusInfo();
                myStatus.Info = update;
                myStatus.Orbit = FlightGlobals.ActiveVessel.orbit;
                myStatus.Color = KLFVessel.GenerateActiveColor(PlayerName);
                myStatus.User = PlayerName;
                myStatus.VesselName = FlightGlobals.ActiveVessel.vesselName;
                myStatus.LastUpdateTime = UnityEngine.Time.realtimeSinceStartup;

                if (PlayerStatus.ContainsKey(PlayerName))
                    PlayerStatus[PlayerName] = myStatus;
                else
                    PlayerStatus.Add(PlayerName, myStatus);

                EnqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID.PrimaryPluginUpdate
                        , KSP.IO.IOUtils.SerializeToBinary(update));
            }
            else
            {
                //Check if the player is building a ship
                bool buildingShip = HighLogic.LoadedSceneIsEditor
                                 && EditorLogic.fetch != null
                                 && EditorLogic.fetch.ship != null
                                 && EditorLogic.fetch.ship.Count > 0
                                 && EditorLogic.fetch.shipNameField != null
                                 && EditorLogic.fetch.shipNameField.Text != null
                                 && EditorLogic.fetch.shipNameField.Text.Length > 0;

                //build status line to send to other clients
                String[] statusArray = null;
                if (buildingShip)
                {
                    statusArray = new String[3];
                    //Vessel name
                    String shipname = EditorLogic.fetch.shipNameField.Text;
                    if (shipname.Length > MaxVesselNameLength)
                        shipname = shipname.Substring(0, MaxVesselNameLength);
                    statusArray[1] = "Building " + shipname;
                    //Vessel details
                    statusArray[2] = "Parts: " + EditorLogic.fetch.ship.Count;
                }
                else
                {
                    statusArray = new String[2];
                    switch (HighLogic.LoadedScene)
                    {
                    case GameScenes.SPACECENTER:
                        statusArray[1] = "At Space Center";
                        break;
                    case GameScenes.EDITOR:
                        statusArray[1] = "In Vehicle Assembly Building";
                        break;
                    case GameScenes.SPH:
                        statusArray[1] = "In Space Plane Hangar";
                        break;
                    case GameScenes.TRACKSTATION:
                        statusArray[1] = "At Tracking Station";
                        break;
                    default:
                        statusArray[1] = String.Empty;
                        break;
                    }
                }

                //Check if player is idle
                if (IsIdle)
                    statusArray[1] = "(Idle) " + statusArray[1];
                statusArray[0] = PlayerName;

                //Serialize the update
                byte[] updateBytes = KSP.IO.IOUtils.SerializeToBinary(statusArray);
                EnqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID.PrimaryPluginUpdate, updateBytes);

                VesselStatusInfo myStatus = StatusArrayToInfo(statusArray);
                if (PlayerStatus.ContainsKey(PlayerName))
                    PlayerStatus[PlayerName] = myStatus;
                else
                    PlayerStatus.Add(PlayerName, myStatus);
            }
        }

        private void WriteSecondaryUpdates()
        {
            if (PerUpdate > 0)
            {//Write the inactive vessels nearest the active vessel to the file
                SortedList<float, Vessel> nearVessels = new SortedList<float, Vessel>();
                foreach (Vessel v in FlightGlobals.Vessels)
                {
                    if (v != FlightGlobals.ActiveVessel)
                    {
                        float distance =
                            (float)Vector3d.Distance(v.GetWorldPos3D()
                                , FlightGlobals.ActiveVessel.GetWorldPos3D());
                        if (distance < InactiveVesselRange)
                        {
                            try
                            {
                                nearVessels.Add(distance, v);
                            }
                            catch (ArgumentException)
                            {
                            }
                        }
                    }
                }

                int countWrittenVessels = 0;

                //Write inactive vessels to file in order of distance from active vessel
                IEnumerator<KeyValuePair<float, Vessel>> enumerator = nearVessels.GetEnumerator();
                while (countWrittenVessels < PerUpdate
                && countWrittenVessels < MaxInactiveVesselsPerUpdate && enumerator.MoveNext())
                {
                    byte[] updateBytes = KSP.IO.IOUtils.SerializeToBinary(GetVesselUpdate(enumerator.Current.Value));
                    EnqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID.SecondaryPluginUpdate, updateBytes);
                    countWrittenVessels++;
                }
            }
        }

        private KLFVesselUpdate GetVesselUpdate(Vessel ves)
        {
            if (ves == null || ves.mainBody == null)
                return null;
            //Create a KLFVesselUpdate from the vessel data
            KLFVesselUpdate update = new KLFVesselUpdate();
            if (ves.vesselName.Length <= MaxVesselNameLength)
                update.Name = ves.vesselName;
            else
                update.Name = ves.vesselName.Substring(0, MaxVesselNameLength);

            update.Player = PlayerName;
            update.Id = ves.id;
            Vector3 pos = ves.mainBody.transform.InverseTransformPoint(ves.GetWorldPos3D());
            Vector3 dir = ves.mainBody.transform.InverseTransformDirection(ves.transform.up);
            Vector3 vel = ves.mainBody.transform.InverseTransformDirection(ves.GetObtVelocity());
            for (int i = 0; i < 3; i++)
            {
                update.Position[i] = pos[i];
                update.Direction[i] = dir[i];
                update.Velocity[i] = vel[i];
            }

            //Determine situation
            if (ves.loaded && ves.GetTotalMass() <= 0.0)
                update.Situation = Situation.Destroyed;
            else
            {
                switch (ves.situation)
                {
                case Vessel.Situations.LANDED:
                    update.Situation = Situation.Landed;
                    break;
                case Vessel.Situations.SPLASHED:
                    update.Situation = Situation.Splashed;
                    break;
                case Vessel.Situations.PRELAUNCH:
                    update.Situation = Situation.Prelaunch;
                    break;
                case Vessel.Situations.SUB_ORBITAL:
                    if (ves.orbit.timeToAp < ves.orbit.period / 2.0)
                        update.Situation = Situation.Ascending;
                    else
                        update.Situation = Situation.Descending;
                    break;
                case Vessel.Situations.ORBITING:
                    update.Situation = Situation.Orbiting;
                    break;
                case Vessel.Situations.ESCAPING:
                    if (ves.orbit.timeToPe > 0.0)
                        update.Situation = Situation.Encountering;
                    else
                        update.Situation = Situation.Escaping;
                    break;
                case Vessel.Situations.DOCKED:
                    update.Situation = Situation.Docked;
                    break;
                case Vessel.Situations.FLYING:
                    update.Situation = Situation.Flying;
                    break;
                default:
                    update.Situation = Situation.Unknown;
                    break;
                }
            }

            if (ves == FlightGlobals.ActiveVessel)
            {
                update.State = State.Active;
                //Set vessel details since it's the active vessel
                update.Detail = GetVesselDetail(ves);
            }
            else if (ves.isCommandable)
                update.State = State.Inactive;
            else
                update.State = State.Dead;

            update.TimeScale = (float)Planetarium.TimeScale;
            update.BodyName = ves.mainBody.bodyName;
            return update;
        }

        private KLFVesselDetail GetVesselDetail(Vessel ves)
        {
            KLFVesselDetail vDetail = new KLFVesselDetail();
            vDetail.Idle = IsIdle;
            vDetail.Mass = ves.GetTotalMass();
            bool isEva = false;
            bool parachutesOpen = false;

            if (ves.isEVA && ves.parts.Count > 0 && ves.parts.First().Modules.Count > 0)
            {//Check if the vessel is an EVA Kerbal
                foreach (PartModule module in ves.parts.First().Modules)
                {
                    if (module is KerbalEVA)
                    {
                        KerbalEVA kerbal = (KerbalEVA)module;
                        vDetail.FuelPercent = (byte)Math.Round(kerbal.Fuel / kerbal.FuelCapacity * 100);
                        vDetail.RcsPercent = byte.MaxValue;
                        vDetail.CrewCount = byte.MaxValue;
                        isEva = true;
                        break;
                    }
                }
            }

            if (!isEva)
            {
                if (ves.GetCrewCapacity() > 0)
                    vDetail.CrewCount = (byte)ves.GetCrewCount();
                else
                    vDetail.CrewCount = byte.MaxValue;

                Dictionary<string, float> fuelDensities = new Dictionary<string, float>();
                Dictionary<string, float> rcsFuelDensities = new Dictionary<string, float>();
                bool hasEngines = false;
                bool hasRcs = false;

                foreach (Part part in ves.parts)
                {
                    foreach (PartModule module in part.Modules)
                    {
                        if (module is ModuleEngines)
                        {//Determine what kinds of fuel this vessel can use and their densities
                            ModuleEngines engine = (ModuleEngines)module;
                            hasEngines = true;

                            foreach (Propellant propellant in engine.propellants)
                            {
                                if (propellant.name == "ElectricCharge" || propellant.name == "IntakeAir")
                                {
                                    continue;
                                }

                                if (!fuelDensities.ContainsKey(propellant.name))
                                    fuelDensities.Add(propellant.name, PartResourceLibrary.Instance.GetDefinition(propellant.id).density);
                            }
                        }

                        if (module is ModuleRCS)
                        {
                            ModuleRCS rcs = (ModuleRCS)module;
                            if (rcs.requiresFuel)
                            {
                                hasRcs = true;
                                if (!rcsFuelDensities.ContainsKey(rcs.resourceName))
                                    rcsFuelDensities.Add(rcs.resourceName, PartResourceLibrary.Instance.GetDefinition(rcs.resourceName).density);
                            }
                        }

                        if (module is ModuleParachute)
                        {
                            ModuleParachute parachute = (ModuleParachute)module;
                            if (parachute.deploymentState == ModuleParachute.deploymentStates.DEPLOYED)
                                parachutesOpen = true;
                        }
                    }
                }

                //Determine how much fuel this vessel has and can hold
                float fuelCapacity = 0.0f;
                float fuelAmount = 0.0f;
                float rcsCapacity = 0.0f;
                float rcsAmount = 0.0f;

                foreach (Part part in ves.parts)
                {
                    if (part != null && part.Resources != null)
                    {
                        foreach (PartResource resource in part.Resources)
                        {
                            float density = 0.0f;
                            //Check that this vessel can use this type of resource as fuel
                            if (hasEngines && fuelDensities.TryGetValue(resource.resourceName, out density))
                            {
                                fuelCapacity += ((float)resource.maxAmount) * density;
                                fuelAmount += ((float)resource.amount) * density;
                            }

                            if (hasRcs && rcsFuelDensities.TryGetValue(resource.resourceName, out density))
                            {
                                rcsCapacity += ((float)resource.maxAmount) * density;
                                rcsAmount += ((float)resource.amount) * density;
                            }
                        }
                    }
                }

                if (hasEngines && fuelCapacity > 0.0f)
                    vDetail.FuelPercent = (byte)Math.Round(fuelAmount / fuelCapacity * 100);
                else
                    vDetail.FuelPercent = byte.MaxValue;

                if (hasRcs && rcsCapacity > 0.0f)
                    vDetail.RcsPercent = (byte)Math.Round(rcsAmount / rcsCapacity * 100);
                else
                    vDetail.RcsPercent = byte.MaxValue;
            }

            //Determine vessel activity
            if (parachutesOpen)
                vDetail.Activity = Activity.Parachuting;
            //Check if the vessel is aerobraking
            if(ves.orbit != null
            && ves.orbit.referenceBody != null
            && ves.orbit.referenceBody.atmosphere
            && ves.orbit.altitude < ves.orbit.referenceBody.maxAtmosphereAltitude)
            {//Vessel inside its body's atmosphere
                switch (ves.situation)
                {
                    case Vessel.Situations.LANDED:
                    case Vessel.Situations.SPLASHED:
                    case Vessel.Situations.SUB_ORBITAL:
                    case Vessel.Situations.PRELAUNCH:
                        break;
                    default:
                        if (ves.situation == Vessel.Situations.ESCAPING
                        || (float)ves.orbit.ApA > ves.orbit.referenceBody.maxAtmosphereAltitude)
                            //If the apoapsis of the orbit is above the atmosphere, vessel is aerobraking
                            vDetail.Activity = Activity.Aerobraking;
                        break;
                }
            }
            //Check if the vessel is docking
            if (vDetail.Activity == Activity.None
            && FlightGlobals.fetch.VesselTarget != null
            && FlightGlobals.fetch.VesselTarget is ModuleDockingNode
            && Vector3.Distance(ves.GetWorldPos3D(), FlightGlobals.fetch.VesselTarget.GetTransform().position) < DockingTargetRange
            )
                vDetail.Activity = Activity.Docking;
            return vDetail;
        }

        private void WritePluginData()
        {
            String currentGameTitle = String.Empty;
            if (HighLogic.CurrentGame != null)
            {
                currentGameTitle = HighLogic.CurrentGame.Title;
                //Remove the (Sandbox) portion of the title
                const String removeS = " (SANDBOX)";
                const String removeC = " (CAREER)";

                if ((currentGameTitle.Length > removeS.Length) && currentGameTitle.Contains(removeS))
                    currentGameTitle = currentGameTitle.Remove(currentGameTitle.Length - removeS.Length);

                if (currentGameTitle.Length > removeC.Length && currentGameTitle.Contains(removeC))
                    currentGameTitle = currentGameTitle.Remove(currentGameTitle.Length - removeC.Length);
            }

            byte[] titleBytes = Encoder.GetBytes(currentGameTitle);
            //Build update byte array
            byte[] updateBytes = new byte[1 + 4 + titleBytes.Length];
            int index = 0;
            //Activity
            updateBytes[index] = IsInFlight ? (byte)1 : (byte)0;
            index++;
            //Game title length
            KLFCommon.IntToBytes(titleBytes.Length).CopyTo(updateBytes, index);
            index += 4;
            //Game title
            titleBytes.CopyTo(updateBytes, index);
            index += titleBytes.Length;

            EnqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID.PluginData, updateBytes);
        }

        private void WriteScreenshotWatchUpdate()
        {
            String watchPlayerName = "";
            if (KLFScreenshotDisplay.WindowEnabled && ShouldDrawGui)
                watchPlayerName = KLFScreenshotDisplay.WatchPlayerName;
            byte[] nameBytes = new UnicodeEncoding().GetBytes(watchPlayerName);

            int currentIndex = -1;
            if (KLFScreenshotDisplay.Screenshot != null && KLFScreenshotDisplay.Screenshot.Player == watchPlayerName)
                currentIndex = KLFScreenshotDisplay.Screenshot.Index;

            byte[] bytes = new byte[8 + nameBytes.Length];
            KLFCommon.IntToBytes(KLFScreenshotDisplay.WatchPlayerIndex).CopyTo(bytes, 0);
            KLFCommon.IntToBytes(currentIndex).CopyTo(bytes, 4);
            nameBytes.CopyTo(bytes, 8);

            EnqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID.ScreenshotWatchUpdate, bytes);
        }

        private VesselStatusInfo StatusArrayToInfo(String[] statusArray)
        {
            if (statusArray != null && statusArray.Length >= StatusArrayMinSize)
            {//Read status array
                VesselStatusInfo status = new VesselStatusInfo();
                status.Info = null;
                status.User = statusArray[0];
                status.VesselName = statusArray[1];
                if (statusArray.Length >= 3)
                    status.DetailText = statusArray[2];
                status.Orbit = null;
                status.LastUpdateTime = UnityEngine.Time.realtimeSinceStartup;
                status.Color = KLFVessel.GenerateActiveColor(status.User);
                return status;
            }
            else
                return new VesselStatusInfo();
        }

        private IEnumerator ShareScreenshot()
        {//Determine the scaled-down dimensions of the screenshot
            int w = 0;
            int h = 0;   
            yield return new WaitForEndOfFrame();

            KLFScreenshotDisplay.Settings.GetBoundedDimensions(Screen.width, Screen.height, ref w, ref h);

            //Read the screen pixels into a texture
            Texture2D fullScreenTex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            fullScreenTex.filterMode = FilterMode.Bilinear;
            fullScreenTex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
            fullScreenTex.Apply();

            RenderTexture renderTex = new RenderTexture(w, h, 24);
            renderTex.useMipMap = false;

            if (KLFGlobalSettings.Instance.SmoothScreens && (Screen.width > w * 2 || Screen.height > h * 2))
            {
                //Blit the full texture to a double-sized texture to improve final quality
                RenderTexture resizeTex = new RenderTexture(w * 2, h * 2, 24);
                Graphics.Blit(fullScreenTex, resizeTex);

                //Blit the double-sized texture to normal-sized texture
                Graphics.Blit(resizeTex, renderTex);
            }
            else
                Graphics.Blit(fullScreenTex, renderTex); //Blit the screen texture to a render texture

            fullScreenTex = null;
            RenderTexture.active = renderTex;
            //Read the pixels from the render texture into a Texture2D
            Texture2D resizedTex = new Texture2D(w, h, TextureFormat.RGB24, false);
            resizedTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            resizedTex.Apply();

            RenderTexture.active = null;
            byte[] data = resizedTex.EncodeToPNG();
            Screenshot screenshot = new Screenshot();
            screenshot.Player = PlayerName;
            if (FlightGlobals.ready && FlightGlobals.ActiveVessel != null)
                screenshot.Description = FlightGlobals.ActiveVessel.vesselName;
            screenshot.Image = data;

            Debug.Log("Sharing screenshot");
            EnqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID.ScreenshotShare, screenshot.ToByteArray());
        }

        private void HandleUpdate(object obj)
        {
            if (obj is KLFVesselUpdate)
                HandleVesselUpdate((KLFVesselUpdate)obj);
            else if (obj is String[])
            {
                String[] statusArray = (String[])obj;
                VesselStatusInfo status = StatusArrayToInfo(statusArray);
                if (status.User != null && status.User.Length > 0)
                    if (PlayerStatus.ContainsKey(status.User))
                        PlayerStatus[status.User] = status;
                    else
                        PlayerStatus.Add(status.User, status);
            }
        }

        private void HandleVesselUpdate(KLFVesselUpdate vesselUpdate)
        {
            if (!IsInFlight)
            {
                //While not in-flight don't create KLF vessel, just store the active vessel status info
                if (vesselUpdate.State == State.Active)
                {
                    VesselStatusInfo status = new VesselStatusInfo();
                    status.Info = vesselUpdate;
                    status.User = vesselUpdate.Player;
                    status.VesselName = vesselUpdate.Name;
                    status.Orbit = null;
                    status.LastUpdateTime = UnityEngine.Time.realtimeSinceStartup;
                    status.Color = KLFVessel.GenerateActiveColor(status.User);
                    if (PlayerStatus.ContainsKey(status.User))
                        PlayerStatus[status.User] = status;
                    else
                        PlayerStatus.Add(status.User, status);
                }
                return; //Don't handle updates while not flying a ship
            }

            //Build the key for the vessel
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(vesselUpdate.Player);
            sb.Append(vesselUpdate.Id.ToString());

            String vesselKey = sb.ToString();
            KLFVessel kVes = null;

            //Try to find the key in the vessel dictionary
            VesselEntry entry;
            if (Vessels.TryGetValue(vesselKey, out entry))
            {
                kVes = entry.Vessel;
                if(kVes == null
                || kVes.GameObj == null
                || kVes.VesselName != vesselUpdate.Name)
                {//Delete the vessel if it's null or needs to be renamed
                    Vessels.Remove(vesselKey);
                    if (kVes != null && kVes.GameObj != null)
                        GameObject.Destroy(kVes.GameObj);
                    kVes = null;
                }
                else
                {
                    //Update the entry's timestamp
                    VesselEntry newEntry = new VesselEntry();
                    newEntry.Vessel = entry.Vessel;
                    newEntry.LastUpdateTime = UnityEngine.Time.realtimeSinceStartup;
                    Vessels[vesselKey] = newEntry;
                }
            }

            if (kVes == null)
            {//Add the vessel to the dictionary
                kVes = new KLFVessel(vesselUpdate.Name, vesselUpdate.Player, vesselUpdate.Id);
                entry = new VesselEntry();
                entry.Vessel = kVes;
                entry.LastUpdateTime = UnityEngine.Time.realtimeSinceStartup;

                if (Vessels.ContainsKey(vesselKey))
                    Vessels[vesselKey] = entry;
                else
                    Vessels.Add(vesselKey, entry);

                /*Queue this update for the next update call because updating a vessel on the same step as
                 * creating it usually causes problems for some reason */
                VesselUpdateQueue.Enqueue(vesselUpdate);
            }
            else
                ApplyVesselUpdate(vesselUpdate, kVes); //Apply the vessel update to the existing vessel

        }

        private void ApplyVesselUpdate(KLFVesselUpdate vesselUpdate, KLFVessel kVes)
        {//Find the CelestialBody that matches the one in the update
            CelestialBody updateBody = null;
            if(kVes.MainBody != null
            && kVes.MainBody.bodyName == vesselUpdate.BodyName)
                updateBody = kVes.MainBody; //already correct body
            else
                foreach (CelestialBody body in FlightGlobals.Bodies)
                    if (body.bodyName == vesselUpdate.BodyName)
                    {
                        updateBody = body;
                        break;
                    }

            if (updateBody != null)
            {//Convert float arrays to Vector3s
                Vector3 pos = new Vector3(vesselUpdate.Position[0], vesselUpdate.Position[1], vesselUpdate.Position[2]);
                Vector3 dir = new Vector3(vesselUpdate.Direction[0], vesselUpdate.Direction[1], vesselUpdate.Direction[2]);
                Vector3 vel = new Vector3(vesselUpdate.Velocity[0], vesselUpdate.Velocity[1], vesselUpdate.Velocity[2]);

                kVes.Info = vesselUpdate;
                kVes.SetOrbitalData(updateBody, pos, vel, dir);
            }

            if (vesselUpdate.State == State.Active)
            {//Update the player status info
                VesselStatusInfo status = new VesselStatusInfo();
                status.Info = vesselUpdate;
                status.User = vesselUpdate.Player;
                status.VesselName = vesselUpdate.Name;
                if (kVes.OrbitValid)
                    status.Orbit = kVes.OrbitRender.driver.orbit;
                status.LastUpdateTime = UnityEngine.Time.realtimeSinceStartup;
                status.Color = KLFVessel.GenerateActiveColor(status.User);

                if (PlayerStatus.ContainsKey(status.User))
                    PlayerStatus[status.User] = status;
                else
                    PlayerStatus.Add(status.User, status);
            }
        }

        private void WriteIntToStream(KSP.IO.FileStream stream, Int32 val)
        {
            stream.Write(KLFCommon.IntToBytes(val), 0, 4);
        }

        private Int32 ReadIntFromStream(KSP.IO.FileStream stream)
        {
            byte[] bytes = new byte[4];
            stream.Read(bytes, 0, 4);
            return KLFCommon.BytesToInt(bytes);
        }

        private void OutMessage(String mes)
        {
            String line = mes.Replace("\n", "");
            if (line.Length > 0)
            {
                EnqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID.ChatSend, Encoder.GetBytes(line));
                KLFChatDisplay.Line("[" + PlayerName + "] " + line);
            }
        }

        public void UpdateVesselPositions()
        {
            foreach (KeyValuePair<String, VesselEntry> pair in Vessels)
                if (pair.Value.Vessel != null)
                    pair.Value.Vessel.UpdatePosition();
        }

        //Interop
        private void ReadClientInterop()
        {
            if (KSP.IO.File.Exists<KLFManager>(InteropClientFilename))
            {
                byte[] bytes = null;
                try
                {
                    bytes = KSP.IO.File.ReadAllBytes<KLFManager>(InteropClientFilename);
                    KSP.IO.File.Delete<KLFManager>(InteropClientFilename);
                }
                catch
                {
                    bytes = null;
                    Debug.LogWarning("*** Unable to read file " + InteropClientFilename);
                }

                if (bytes != null && bytes.Length > 4)
                {
                    //Read the file-format version (First 4 bytes)
                    int fileVersion = KLFCommon.BytesToInt(bytes, 0);
                    if (fileVersion != KLFCommon.FileFormatVersion)
                    {
                        Debug.LogError("KLF Client incompatible with plugin");
                        return;
                    }
                    //Parse the messages (after first 4 bytes)
                    int index = 4;
                    while (index < bytes.Length - KLFCommon.InteropMessageHeaderLength)
                    {
                        //Read the message id (or type) and verify
                        int idInt = KLFCommon.BytesToInt(bytes, index);
                        KLFCommon.ClientInteropMessageID id = KLFCommon.ClientInteropMessageID.Null;
                        if(idInt >= 0
                        && idInt < Enum.GetValues(typeof(KLFCommon.ClientInteropMessageID)).Length)
                            id = (KLFCommon.ClientInteropMessageID)idInt;

                        //Read the length of the message data
                        int dataLength = KLFCommon.BytesToInt(bytes, index + 4);
                        index += KLFCommon.InteropMessageHeaderLength;
                        if (dataLength <= 0)
                            HandleInteropMessage(id, null);
                        else if (dataLength <= (bytes.Length - index))
                        {
                            //Copy the message data
                            byte[] data = new byte[dataLength];
                            Array.Copy(bytes, index, data, 0, data.Length);
                            HandleInteropMessage(id, data);
                        }
                        if (dataLength > 0)
                            index += dataLength;
                    }
                }
            }
        }

        private bool WritePluginInterop()
        {
            if (InteropOutQueue.Count > 0
            && !KSP.IO.File.Exists<KLFManager>(InteropPluginFilename))
            {
                try
                {
                    KSP.IO.FileStream outStream = null;
                    try
                    {
                        outStream = KSP.IO.File.Create<KLFManager>(InteropPluginFilename);
                        outStream.Lock(0, long.MaxValue);
                        //Write file-format version
                        outStream.Write(KLFCommon.IntToBytes(KLFCommon.FileFormatVersion), 0, 4);
                        while (InteropOutQueue.Count > 0)
                        {
                            byte[] message = InteropOutQueue.Dequeue();
                            outStream.Write(message, 0, message.Length);
                        }
                        outStream.Unlock(0, long.MaxValue);
                        outStream.Flush();
                        return true;//success
                    }
                    finally
                    {
                        if (outStream != null)
                            outStream.Dispose();
                    }
                }
                catch { }
            }
            return false;//failure
        }

        private void HandleInteropMessage(KLFCommon.ClientInteropMessageID id, byte[] data)
        {
            switch (id)
            {
                case KLFCommon.ClientInteropMessageID.ChatReceive:
                    if (data != null)
                        KLFChatDisplay.Line(Encoder.GetString(data));
                    break;

                case KLFCommon.ClientInteropMessageID.ClientData:
                    if (data != null && data.Length > 9)
                    {
                        //Read inactive vessels per update count
                        PerUpdate = data[0];
                        //Read screenshot height
                        KLFScreenshotDisplay.Settings.MaxHeight = KLFCommon.BytesToInt(data, 1);
                        UpdateInterval = ((float)KLFCommon.BytesToInt(data, 5))/1000.0f;
                        //Read username
                        PlayerName = Encoder.GetString(data, 9, data.Length - 9);
                    }
                    break;

                case KLFCommon.ClientInteropMessageID.PluginUpdate:
                    if (data != null)
                        HandleUpdate(KSP.IO.IOUtils.DeserializeFromBinary(data));
                    break;

                case KLFCommon.ClientInteropMessageID.ScreenshotReceive:
                    if (data != null)
                    {
                        Debug.Log("Received screenshot");
                        KLFScreenshotDisplay.Screenshot.SetFromByteArray(data);

                        if (KLFScreenshotDisplay.Screenshot.Image.Length <= KLFScreenshotDisplay.Settings.MaxNumBytes)
                        {
                            if(KLFScreenshotDisplay.Texture == null)
                                KLFScreenshotDisplay.Texture = new Texture2D(4, 4, TextureFormat.RGB24, false, true);
                            if(KLFScreenshotDisplay.Texture.LoadImage(KLFScreenshotDisplay.Screenshot.Image))
                            {
                                KLFScreenshotDisplay.Texture.Apply();

                                //Make sure the screenshot texture does not exceed the size limits
                                if(KLFScreenshotDisplay.Texture.width > KLFScreenshotDisplay.Settings.MaxWidth
                                || KLFScreenshotDisplay.Texture.height > KLFScreenshotDisplay.Settings.MaxHeight)
                                    KLFScreenshotDisplay.Screenshot.Clear();
                            }
                            else
                                KLFScreenshotDisplay.Screenshot.Clear();
                            KLFScreenshotDisplay.Screenshot.Image = null;
                        }
                    }
                    break;
            }
        }

        private void EnqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID id, byte[] data)
        {
            int msgDataLength = 0;
            if (data != null)
                msgDataLength = data.Length;

            byte[] messageBytes = new byte[KLFCommon.InteropMessageHeaderLength + msgDataLength];

            KLFCommon.IntToBytes((int)id).CopyTo(messageBytes, 0);
            KLFCommon.IntToBytes(msgDataLength).CopyTo(messageBytes, 4);
            if (data != null)
                data.CopyTo(messageBytes, KLFCommon.InteropMessageHeaderLength);

            InteropOutQueue.Enqueue(messageBytes);

            //Enforce max queue size
            while (InteropOutQueue.Count > InteropMaxQueueSize)
                InteropOutQueue.Dequeue();
        }

        //Settings

        private void SaveGlobalSettings()
        {
            //Get the global settings
            KLFGlobalSettings.Instance.InfoDisplayWindowX = KLFInfoDisplay.InfoWindowPos.x;
            KLFGlobalSettings.Instance.InfoDisplayWindowY = KLFInfoDisplay.InfoWindowPos.y;

            KLFGlobalSettings.Instance.ScreenshotDisplayWindowX = KLFScreenshotDisplay.WindowPos.x;
            KLFGlobalSettings.Instance.ScreenshotDisplayWindowY = KLFScreenshotDisplay.WindowPos.y;

            KLFGlobalSettings.Instance.ChatDisplayWindowX = KLFChatDisplay.WindowPos.x;
            KLFGlobalSettings.Instance.ChatDisplayWindowY = KLFChatDisplay.WindowPos.y;

            try
            {//Serialize global settings to file
                byte[] serialized = KSP.IO.IOUtils.SerializeToBinary(KLFGlobalSettings.Instance);
                KSP.IO.File.WriteAllBytes<KLFManager>(serialized, GlobalSettingsFilename);
            }
            catch (KSP.IO.IOException) {}
            LastGlobalSettingSaveTime = UnityEngine.Time.realtimeSinceStartup;
        }

        public void Awake()
        {//MonoBehaviour
            DontDestroyOnLoad(this);
            CancelInvoke();
            InvokeRepeating("UpdateStep", 1 / 60.0f, 1 / 60.0f);

            if(KSP.IO.File.Exists<KLFManager>(InteropClientFilename))
            {
                try
                {//delete any existing transaction file
                    KSP.IO.File.Delete<KLFManager>(InteropClientFilename);
                }
                catch {}
            }

            //load global settings
            try
            {
                if (KSP.IO.File.Exists<KLFManager>(GlobalSettingsFilename))
                {//Deserialize global settings from file
                    byte[] bytes = KSP.IO.File.ReadAllBytes<KLFManager>(GlobalSettingsFilename);
                    object deserialized = KSP.IO.IOUtils.DeserializeFromBinary(bytes);
                    if (deserialized is KLFGlobalSettings)
                    {
                        KLFGlobalSettings.Instance = (KLFGlobalSettings)deserialized;

                        KLFInfoDisplay.InfoWindowPos.x = KLFGlobalSettings.Instance.InfoDisplayWindowX;
                        KLFInfoDisplay.InfoWindowPos.y = KLFGlobalSettings.Instance.InfoDisplayWindowY;

                        KLFScreenshotDisplay.WindowPos.x = KLFGlobalSettings.Instance.ScreenshotDisplayWindowX;
                        KLFScreenshotDisplay.WindowPos.y = KLFGlobalSettings.Instance.ScreenshotDisplayWindowY;

                        KLFChatDisplay.WindowPos.x = KLFGlobalSettings.Instance.ChatDisplayWindowX;
                        KLFChatDisplay.WindowPos.y = KLFGlobalSettings.Instance.ChatDisplayWindowY;
                    }
                }
            }
            catch (KSP.IO.IOException)
            {
            }
        }

        public void Update()
        {
            if (HighLogic.LoadedScene == GameScenes.LOADING)
                return; //Don't do anything while the game is loading
            if (RenderManager == null)
                RenderManager = (RenderingManager) FindObjectOfType(typeof(RenderingManager));
            if (PlanetariumCam == null)
                PlanetariumCam = (PlanetariumCamera)FindObjectOfType(typeof(PlanetariumCamera));
            //key events
            if (Input.GetKeyDown(KLFGlobalSettings.Instance.GuiToggleKey))
                KLFInfoDisplay.InfoDisplayActive = !KLFInfoDisplay.InfoDisplayActive;
            if (Input.GetKeyDown(KLFGlobalSettings.Instance.ScreenshotKey) || SharingScreenshot)
                ShareScreenshot();
            if (Input.GetKeyDown(KLFGlobalSettings.Instance.ChatKey))
                KLFGlobalSettings.Instance.ChatWindowEnabled = !KLFGlobalSettings.Instance.ChatWindowEnabled;
            if (Input.GetKeyDown(KLFGlobalSettings.Instance.ViewKey))
                KLFScreenshotDisplay.WindowEnabled = !KLFScreenshotDisplay.WindowEnabled;

            //idle monitor
            if (Input.anyKeyDown)
                LastKeyPressTime = UnityEngine.Time.realtimeSinceStartup;

            //Handle custom key-bindings
            if (MappingGUIToggleKey)
            {
                KeyCode key = KeyCode.F7;
                if (GetAnyKeyDown(ref key))
                {
                    KLFGlobalSettings.Instance.GuiToggleKey = key;
                    MappingGUIToggleKey = false;
                }
            }
            if (MappingScreenshotKey)
            {
                KeyCode key = KeyCode.F8;
                if (GetAnyKeyDown(ref key))
                {
                    KLFGlobalSettings.Instance.ScreenshotKey = key;
                    MappingScreenshotKey = false;
                }
            }
            if (MappingChatKey)
            {
                KeyCode key = KeyCode.F9;
                if (GetAnyKeyDown(ref key))
                {
                    KLFGlobalSettings.Instance.ChatKey = key;
                    MappingChatKey = false;
                }
            }
            if (MappingViewKey)
            {
                KeyCode key = KeyCode.F10;
                if (GetAnyKeyDown(ref key))
                {
                    KLFGlobalSettings.Instance.ViewKey = key;
                    MappingViewKey = false;
                }
            }
        }

        //GUI
        public void OnGUI()
        {//MonoBehavior
            DrawGUI();
        }
        public void DrawGUI()
        {
            if (ShouldDrawGui)
            {//Init info display options
                if (KLFInfoDisplay.LayoutOptions == null)
                    KLFInfoDisplay.LayoutOptions = new GUILayoutOption[6];

                KLFInfoDisplay.LayoutOptions[0] = GUILayout.ExpandHeight(true);
                KLFInfoDisplay.LayoutOptions[1] = GUILayout.ExpandWidth(true);

                if (KLFInfoDisplay.InfoDisplayMinimized)
                {
                    KLFInfoDisplay.LayoutOptions[2] = GUILayout.MinHeight(KLFInfoDisplay.WindowHeightMinimized);
                    KLFInfoDisplay.LayoutOptions[3] = GUILayout.MaxHeight(KLFInfoDisplay.WindowHeightMinimized);

                    KLFInfoDisplay.LayoutOptions[4] = GUILayout.MinWidth(KLFInfoDisplay.WindowWidthMinimized);
                    KLFInfoDisplay.LayoutOptions[5] = GUILayout.MaxWidth(KLFInfoDisplay.WindowWidthMinimized);
                }
                else
                {
                    if (KLFGlobalSettings.Instance.InfoDisplayBig)
                    {
                        KLFInfoDisplay.LayoutOptions[4] = GUILayout.MinWidth(KLFInfoDisplay.WindowWidthBig);
                        KLFInfoDisplay.LayoutOptions[5] = GUILayout.MaxWidth(KLFInfoDisplay.WindowWidthBig);

                        KLFInfoDisplay.LayoutOptions[2] = GUILayout.MinHeight(KLFInfoDisplay.WindowHeightBig);
                        KLFInfoDisplay.LayoutOptions[3] = GUILayout.MaxHeight(KLFInfoDisplay.WindowHeightBig);
                    }
                    else
                    {
                        KLFInfoDisplay.LayoutOptions[4] = GUILayout.MinWidth(KLFInfoDisplay.WindowWidthDefault);
                        KLFInfoDisplay.LayoutOptions[5] = GUILayout.MaxWidth(KLFInfoDisplay.WindowWidthDefault);

                        KLFInfoDisplay.LayoutOptions[2] = GUILayout.MinHeight(KLFInfoDisplay.WindowHeight);
                        KLFInfoDisplay.LayoutOptions[3] = GUILayout.MaxHeight(KLFInfoDisplay.WindowHeight);
                    }
                }

                //Init chat display options
                if (KLFChatDisplay.LayoutOptions == null)
                    KLFChatDisplay.LayoutOptions = new GUILayoutOption[2];
                KLFChatDisplay.LayoutOptions[0] = GUILayout.MinWidth(KLFChatDisplay.WindowWidth);
                KLFChatDisplay.LayoutOptions[1] = GUILayout.MaxWidth(KLFChatDisplay.WindowWidth);

                //Init screenshot display options
                if (KLFScreenshotDisplay.LayoutOptions == null)
                    KLFScreenshotDisplay.LayoutOptions = new GUILayoutOption[2];
                KLFScreenshotDisplay.LayoutOptions[0] = GUILayout.MaxHeight(KLFScreenshotDisplay.MinWindowHeight);
                KLFScreenshotDisplay.LayoutOptions[1] = GUILayout.MaxWidth(KLFScreenshotDisplay.MinWindowWidth);

                GUI.skin = HighLogic.Skin;

                KLFInfoDisplay.InfoWindowPos =
                    GUILayout.Window( 999999
                                    , KLFInfoDisplay.InfoWindowPos
                                    , InfoDisplayWindow
                                    , KLFInfoDisplay.InfoDisplayMinimized ? "KLF" : "Kerbal LiveFeed v" + KLFCommon.ProgramVersion + " ("+KLFGlobalSettings.Instance.GuiToggleKey+")"
                                    , KLFInfoDisplay.LayoutOptions
                                    );

                if (KLFScreenshotDisplay.WindowEnabled)
                    KLFScreenshotDisplay.WindowPos =
                        GUILayout.Window( 999998
                                        , KLFScreenshotDisplay.WindowPos
                                        , ScreenshotWindow
                                        , "Kerbal LiveFeed Viewer"
                                        , KLFScreenshotDisplay.LayoutOptions
                                        );

                if (KLFGlobalSettings.Instance.ChatWindowEnabled)
                    KLFChatDisplay.WindowPos =
                        GUILayout.Window( 999997
                                        , KLFChatDisplay.WindowPos
                                        , ChatWindow
                                        , "Kerbal LiveFeed Chat"
                                        , KLFChatDisplay.LayoutOptions
                                        );

                KLFInfoDisplay.InfoWindowPos = EnforceWindowBoundaries(KLFInfoDisplay.InfoWindowPos);
                KLFScreenshotDisplay.WindowPos = EnforceWindowBoundaries(KLFScreenshotDisplay.WindowPos);
                KLFChatDisplay.WindowPos = EnforceWindowBoundaries(KLFChatDisplay.WindowPos);
            }
        }

        private void InfoDisplayWindow(int windowID)
        {
            GUILayout.BeginVertical();
            bool minimized = KLFInfoDisplay.InfoDisplayMinimized;
            bool big = KLFGlobalSettings.Instance.InfoDisplayBig;

            if (!minimized)
                GUILayout.BeginHorizontal();
            KLFInfoDisplay.InfoDisplayMinimized =
                GUILayout.Toggle( KLFInfoDisplay.InfoDisplayMinimized
                                , KLFInfoDisplay.InfoDisplayMinimized ? "Max" : "Min"
                                , GUI.skin.button
                                );

            if (!minimized)
            {
                KLFInfoDisplay.InfoDisplayDetailed =
                    GUILayout.Toggle( KLFInfoDisplay.InfoDisplayDetailed
                                    , "Detail"
                                    , GUI.skin.button
                                    );
                KLFGlobalSettings.Instance.InfoDisplayBig =
                    GUILayout.Toggle( KLFGlobalSettings.Instance.InfoDisplayBig
                                    , "Big"
                                    , GUI.skin.button
                                    );
                KLFInfoDisplay.InfoDisplayOptions =
                    GUILayout.Toggle( KLFInfoDisplay.InfoDisplayOptions
                                    , "Options"
                                    , GUI.skin.button
                                    );
                GUILayout.EndHorizontal();

                KLFInfoDisplay.InfoScrollPos = GUILayout.BeginScrollView(KLFInfoDisplay.InfoScrollPos);
                GUILayout.BeginVertical();

                //Init label styles
                PlayerNameStyle = new GUIStyle(GUI.skin.label);
                PlayerNameStyle.normal.textColor = Color.white;
                PlayerNameStyle.hover.textColor = Color.white;
                PlayerNameStyle.active.textColor = Color.white;
                PlayerNameStyle.alignment = TextAnchor.MiddleLeft;
                PlayerNameStyle.margin = new RectOffset(0, 0, 2, 0);
                PlayerNameStyle.padding = new RectOffset(0, 0, 0, 0);
                PlayerNameStyle.stretchWidth = true;
                PlayerNameStyle.fontStyle = FontStyle.Bold;

                VesselNameStyle = new GUIStyle(GUI.skin.label);
                VesselNameStyle.normal.textColor = Color.white;
                VesselNameStyle.stretchWidth = true;
                VesselNameStyle.fontStyle = FontStyle.Bold;
                if (big)
                {
                    VesselNameStyle.margin = new RectOffset(0, 4, 2, 0);
                    VesselNameStyle.alignment = TextAnchor.LowerRight;
                }
                else
                {
                    VesselNameStyle.margin = new RectOffset(4, 0, 0, 0);
                    VesselNameStyle.alignment = TextAnchor.LowerLeft;
                }

                VesselNameStyle.padding = new RectOffset(0, 0, 0, 0);

                StateTextStyle = new GUIStyle(GUI.skin.label);
                StateTextStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
                StateTextStyle.margin = new RectOffset(4, 0, 0, 0);
                StateTextStyle.padding = new RectOffset(0, 0, 0, 0);
                StateTextStyle.stretchWidth = true;
                StateTextStyle.fontStyle = FontStyle.Normal;
                StateTextStyle.fontSize = 12;

                //Write vessel's statuses
                foreach (KeyValuePair<String, VesselStatusInfo> pair in PlayerStatus)
                    VesselStatusLabels(pair.Value, big);

                GUILayout.EndVertical();
                GUILayout.EndScrollView();

                GUILayout.BeginHorizontal();
                KLFGlobalSettings.Instance.ChatWindowEnabled =
                    GUILayout.Toggle( KLFGlobalSettings.Instance.ChatWindowEnabled
                                    , "Chat ("+KLFGlobalSettings.Instance.ChatKey+")"
                                    , GUI.skin.button
                                    );
                KLFScreenshotDisplay.WindowEnabled =
                    GUILayout.Toggle( KLFScreenshotDisplay.WindowEnabled
                                    , "Viewer ("+KLFGlobalSettings.Instance.ViewKey+")"
                                    , GUI.skin.button
                                    );

                //Event
                if (GUILayout.Button("Share Screen ("+KLFGlobalSettings.Instance.ScreenshotKey+")"))
                    SharingScreenshot = true;
                GUILayout.EndHorizontal();

                if (KLFInfoDisplay.InfoDisplayOptions)
                {
                    //Settings
                    GUILayout.Label("Settings");
                    GUILayout.BeginHorizontal();
                    KLFGlobalSettings.Instance.SmoothScreens =
                        GUILayout.Toggle( KLFGlobalSettings.Instance.SmoothScreens
                                        , "Smooth Screenshots"
                                        , GUI.skin.button
                                        );
                    KLFGlobalSettings.Instance.ChatColors =
                        GUILayout.Toggle( KLFGlobalSettings.Instance.ChatColors
                                        , "Chat Colors"
                                        , GUI.skin.button
                                        );

                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();

                    KLFGlobalSettings.Instance.ShowOtherShips =
                        GUILayout.Toggle( KLFGlobalSettings.Instance.ShowOtherShips
                                        , "Ships Icons"
                                        , GUI.skin.button
                                        );
                    KLFGlobalSettings.Instance.ShowInactiveShips =
                        GUILayout.Toggle( KLFGlobalSettings.Instance.ShowInactiveShips
                                        , "Inactive Icons"
                                        , GUI.skin.button
                                        );
                    KLFGlobalSettings.Instance.ShowOrbits =
                        GUILayout.Toggle( KLFGlobalSettings.Instance.ShowOrbits
                                        , "Orbits"
                                        , GUI.skin.button
                                        );
                    GUILayout.EndHorizontal();

                    //Key mapping
                    GUILayout.Label("Key-Bindings");
                    GUILayout.BeginHorizontal();

                    MappingGUIToggleKey =
                        GUILayout.Toggle( MappingGUIToggleKey
                                        , MappingGUIToggleKey ? "Press key" : "Menu Toggle: " + KLFGlobalSettings.Instance.GuiToggleKey
                                        , GUI.skin.button
                                        );
                    MappingScreenshotKey =
                        GUILayout.Toggle( MappingScreenshotKey
                                        , MappingScreenshotKey ? "Press key" : "Screenshot: " + KLFGlobalSettings.Instance.ScreenshotKey
                                        , GUI.skin.button
                                        );

                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();

                    MappingChatKey =
                        GUILayout.Toggle( MappingChatKey
                                        , MappingChatKey ? "Press key" : "Chat Toggle: " + KLFGlobalSettings.Instance.ChatKey
                                        , GUI.skin.button
                                        );
                    MappingViewKey =
                        GUILayout.Toggle( MappingViewKey
                                        , MappingViewKey ? "Press key" : "Viewer Toggle: " + KLFGlobalSettings.Instance.ViewKey
                                        , GUI.skin.button
                                        );
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void ScreenshotWindow(int windowID)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayoutOption[] screenshotBoxOptions = new GUILayoutOption[4];
            screenshotBoxOptions[0] = GUILayout.MinWidth(KLFScreenshotDisplay.Settings.MaxWidth);
            screenshotBoxOptions[1] = GUILayout.MaxWidth(KLFScreenshotDisplay.Settings.MaxWidth);
            screenshotBoxOptions[2] = GUILayout.MinHeight(KLFScreenshotDisplay.Settings.MaxHeight);
            screenshotBoxOptions[3] = GUILayout.MaxHeight(KLFScreenshotDisplay.Settings.MaxHeight);

            //Init label styles
            ScreenshotDescriptionStyle = new GUIStyle(GUI.skin.label);
            ScreenshotDescriptionStyle.normal.textColor = Color.white;
            ScreenshotDescriptionStyle.alignment = TextAnchor.MiddleCenter;
            ScreenshotDescriptionStyle.stretchWidth = true;
            ScreenshotDescriptionStyle.fontStyle = FontStyle.Normal;
            ScreenshotDescriptionStyle.margin.bottom = 0;
            ScreenshotDescriptionStyle.margin.top = 0;
            ScreenshotDescriptionStyle.padding.bottom = 0;
            ScreenshotDescriptionStyle.padding.top = 4;

            //Screenshot
            if (KLFScreenshotDisplay.Texture != null)
            {
                GUILayout.Box(KLFScreenshotDisplay.Texture, screenshotBoxOptions);
                GUILayout.BeginHorizontal();

                //Nav buttons
                if (KLFScreenshotDisplay.Screenshot != null
                && KLFScreenshotDisplay.Screenshot.Player == KLFScreenshotDisplay.WatchPlayerName)
                {
                    bool pressed = false;
                    if (KLFScreenshotDisplay.Screenshot.Index > 0
                    && GUILayout.Button("Prev", GUILayout.ExpandWidth(false)))
                    {
                        KLFScreenshotDisplay.WatchPlayerIndex = KLFScreenshotDisplay.Screenshot.Index - 1;
                        pressed = true;
                    }

                    if (GUILayout.Button("Next", GUILayout.ExpandWidth(false)))
                    {
                        KLFScreenshotDisplay.WatchPlayerIndex = KLFScreenshotDisplay.Screenshot.Index + 1;
                        pressed = true;
                    }
                    if (pressed)
                        WriteScreenshotWatchUpdate();
                }

                //Description
                StringBuilder sb = new StringBuilder();
                sb.Append(KLFScreenshotDisplay.Screenshot.Player);
                if (KLFScreenshotDisplay.Screenshot.Description.Length > 0)
                {
                    sb.Append(" - ");
                    sb.Append(KLFScreenshotDisplay.Screenshot.Description);
                }
                GUILayout.Label(sb.ToString(), ScreenshotDescriptionStyle);
                GUILayout.EndHorizontal();
            }
            else
                GUILayout.Box(GUIContent.none, screenshotBoxOptions);
            GUILayoutOption[] userListOptions = new GUILayoutOption[1];
            userListOptions[0] = GUILayout.MinWidth(150);
            GUILayout.EndVertical();

            //User list
            KLFScreenshotDisplay.ScrollPos =
                GUILayout.BeginScrollView( KLFScreenshotDisplay.ScrollPos
                                         , userListOptions);
            GUILayout.BeginVertical();

            foreach (KeyValuePair<String, VesselStatusInfo> pair in PlayerStatus)
            {
                ScreenshotWatchButton(pair.Key);
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }

        private void ChatWindow(int windowID)
        {

            //Init label styles
            ChatLineStyle = new GUIStyle(GUI.skin.label);
            ChatLineStyle.normal.textColor = Color.white;
            ChatLineStyle.margin = new RectOffset(0, 0, 0, 0);
            ChatLineStyle.padding = new RectOffset(0, 0, 0, 0);
            ChatLineStyle.alignment = TextAnchor.LowerLeft;
            ChatLineStyle.wordWrap = true;
            ChatLineStyle.stretchWidth = true;
            ChatLineStyle.fontStyle = FontStyle.Normal;

            GUILayoutOption[] entryFieldOptions = new GUILayoutOption[1];
            entryFieldOptions[0] = GUILayout.MaxWidth(KLFChatDisplay.WindowWidth-58);

            GUIStyle chatEntryStyle = new GUIStyle(GUI.skin.textField);
            chatEntryStyle.stretchWidth = true;
            GUILayout.BeginVertical();
            //Mode toggles
            GUILayout.BeginHorizontal();
            KLFGlobalSettings.Instance.ChatWindowWide =
                GUILayout.Toggle( KLFGlobalSettings.Instance.ChatWindowWide
                                , "Wide"
                                , GUI.skin.button);
            KLFChatDisplay.DisplayCommands =
                GUILayout.Toggle( KLFChatDisplay.DisplayCommands
                                , "Help"
                                , GUI.skin.button);
            GUILayout.EndHorizontal();
            //Commands
            if (KLFChatDisplay.DisplayCommands)
            {
                ChatLineStyle.normal.textColor = Color.white;

                GUILayout.Label("/quit - Leave the server", ChatLineStyle);
                GUILayout.Label(KLFCommon.ShareCraftCommand + " <craftname> - Share a craft", ChatLineStyle);
                GUILayout.Label(KLFCommon.GetCraftCommand + " <playername> - Get the craft the player last shared", ChatLineStyle);
                GUILayout.Label("/list - View players on the server", ChatLineStyle);
            }
            KLFChatDisplay.ScrollPos = GUILayout.BeginScrollView(KLFChatDisplay.ScrollPos);
            //Chat text
            GUILayout.BeginVertical();
            foreach (KLFChatDisplay.ChatLine line in KLFChatDisplay.ChatLineQueue)
            {
                if (KLFGlobalSettings.Instance.ChatColors)
                    ChatLineStyle.normal.textColor = line.Color;
                GUILayout.Label(line.Message, ChatLineStyle);
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            KLFChatDisplay.ChatEntryString =
                GUILayout.TextField( KLFChatDisplay.ChatEntryString
                                   , KLFChatDisplay.MaxChatLineLength
                                   , chatEntryStyle
                                   , entryFieldOptions);
            if(KLFChatDisplay.ChatEntryString.Contains('\n')
            || GUILayout.Button("Send"))
            {
                OutMessage(KLFChatDisplay.ChatEntryString);
                KLFChatDisplay.ChatEntryString = String.Empty;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void VesselStatusLabels(VesselStatusInfo status, bool big)
        {
            bool namePressed = false;
            PlayerNameStyle.normal.textColor = status.Color * 0.75f + Color.white * 0.25f;

            if (big)
                GUILayout.BeginHorizontal();
            if (status.User != null)
                namePressed |= GUILayout.Button(status.User, PlayerNameStyle);
            if (status.VesselName != null && status.VesselName.Length > 0)
            {
                String vName = status.VesselName;
                if (status.Info != null && status.Info.Detail != null && status.Info.Detail.Idle)
                    vName = "(Idle) " + vName;
                namePressed |= GUILayout.Button(vName, VesselNameStyle);
            }

            if (big)
                GUILayout.EndHorizontal();

            //Build the detail text
            StringBuilder sb = new StringBuilder();

            //Check if the status has specific detail text
            if (status.DetailText != null && status.DetailText.Length > 0 && KLFInfoDisplay.InfoDisplayDetailed)
                sb.Append(status.DetailText);
            else if (status.Info != null && status.Info.Detail != null)
            {

                bool exploded = false;
                bool situationDetermined = false;

                if(status.Info.Situation == Situation.Destroyed
                || status.Info.Detail.Mass <= 0.0f)
                {
                    sb.Append("Exploded at ");
                    exploded = true;
                    situationDetermined = true;
                }
                else
                {
                    //Check if the vessel's activity overrides the situation
                    switch (status.Info.Detail.Activity)
                    {
                    case Activity.Aerobraking:
                        sb.Append("Aerobraking at ");
                        situationDetermined = true;
                        break;
                    case Activity.Docking:
                        if (KLFVessel.SituationIsGrounded(status.Info.Situation))
                            sb.Append("Docking on ");
                        else
                            sb.Append("Docking above ");
                        situationDetermined = true;
                        break;
                    case Activity.Parachuting:
                        sb.Append("Parachuting to ");
                        situationDetermined = true;
                        break;
                    }

                    if (!situationDetermined)
                    {
                        switch (status.Info.Situation)
                        {
                        case Situation.Docked:
                            sb.Append("Docked at ");
                            break;
                        case Situation.Encountering:
                            sb.Append("Encountering ");
                            break;
                        case Situation.Escaping:
                            sb.Append("Escaping ");
                            break;
                        case Situation.Flying:
                            sb.Append("Flying at ");
                            break;
                        case Situation.Landed:
                            sb.Append("Landed at ");
                            break;
                        case Situation.Orbiting:
                            sb.Append("Orbiting ");
                            break;
                        case Situation.Prelaunch:
                            sb.Append("Prelaunch at ");
                            break;
                        case Situation.Splashed:
                            sb.Append("Splashed at ");
                            break;
                        case Situation.Ascending:
                            sb.Append("Ascending from ");
                            break;
                        case Situation.Descending:
                            sb.Append("Descending to ");
                            break;
                        }
                    }
                }

                sb.Append(status.Info.BodyName);
                if (!exploded && KLFInfoDisplay.InfoDisplayDetailed)
                {

                    bool showMass = status.Info.Detail.Mass >= 0.05f;
                    bool showFuel = status.Info.Detail.FuelPercent < byte.MaxValue;
                    bool showRcs = status.Info.Detail.RcsPercent < byte.MaxValue;
                    bool showCrew = status.Info.Detail.CrewCount < byte.MaxValue;

                    if (showMass || showFuel || showRcs || showCrew)
                        sb.Append(" - ");
                    if (showMass)
                    {
                        sb.Append("Mass: ");
                        sb.Append(status.Info.Detail.Mass.ToString("0.0"));
                        sb.Append(' ');
                    }
                    if (showFuel)
                    {
                        sb.Append("Fuel: ");
                        sb.Append(status.Info.Detail.FuelPercent);
                        sb.Append("% ");
                    }
                    if (showRcs)
                    {
                        sb.Append("RCS: ");
                        sb.Append(status.Info.Detail.RcsPercent);
                        sb.Append("% ");
                    }
                    if (showCrew)
                    {
                        sb.Append("Crew: ");
                        sb.Append(status.Info.Detail.CrewCount);
                    }
                }
            }

            if (sb.Length > 0)
                GUILayout.Label(sb.ToString(), StateTextStyle);
            if (namePressed
            && HighLogic.LoadedSceneHasPlanetarium && PlanetariumCam != null
            && status.Info != null
            && status.Info.BodyName.Length > 0)
            {//If name was pressed, focus on that players' reference body
                if (!MapView.MapIsEnabled)
                    MapView.EnterMapView();
                foreach (MapObject target in PlanetariumCam.targets)
                {
                    if (target.name == status.Info.BodyName)
                    {
                        PlanetariumCam.SetTarget(target);
                        break;
                    }
                }
            }
        }

        private void ScreenshotWatchButton(String name)
        {
            bool playerSelected = GUILayout.Toggle(KLFScreenshotDisplay.WatchPlayerName == name, name, GUI.skin.button);
            if (playerSelected != (KLFScreenshotDisplay.WatchPlayerName == name))
            {
                if (KLFScreenshotDisplay.WatchPlayerName != name)
                    KLFScreenshotDisplay.WatchPlayerName = name; //Set watch player name
                else
                    KLFScreenshotDisplay.WatchPlayerName = String.Empty;
                KLFScreenshotDisplay.WatchPlayerIndex = -1;
                WriteScreenshotWatchUpdate();
            }
        }

        private Rect EnforceWindowBoundaries(Rect window)
        {
            const int padding = 20;
            if (window.x < -window.width + padding)
                window.x = -window.width + padding;
            if (window.x > Screen.width - padding)
                window.x = Screen.width - padding;
            if (window.y < -window.height + padding)
                window.y = -window.height + padding;
            if (window.y > Screen.height - padding)
                window.y = Screen.height - padding;
            return window;
        }
    }
}
