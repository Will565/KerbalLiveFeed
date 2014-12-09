using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KLF
{
    public class KLFVessel
    {
        //Properties
        public KLFVesselInfo Info;
        public String VesselName
        {
            private set;
            get;
        }
        public String UserName
        {
            private set;
            get;
        }
        public Guid Id
        {
            private set;
            get;
        }
        public Vector3 LocalDirection
        {
            private set;
            get;
        }
        public Vector3 LocalPosition
        {
            private set;
            get;
        }
        public Vector3 LocalVelocity
        {
            private set;
            get;
        }
        public Vector3 TranslationFromBody
        {
            private set;
            get;
        }
        public Vector3 WorldDirection
        {
            private set;
            get;
        }
        public Vector3 WorldPosition
        {
            get
            {
                if (!OrbitValid)
                    return Vector3.zero;
                if (MainBody != null)
                {
                    if (SituationIsGrounded(Info.Situation))
                    {//Vessel is fixed in relation to body
                        return MainBody.transform.TransformPoint(LocalPosition);
                    }
                    else
                    {//Calculate vessel's position at current time
                        double time = AdjustedUT;

                        if (MainBody.referenceBody != null && MainBody.referenceBody != MainBody && MainBody.orbit != null)
                        {//Adjust for movement of the vessel's parent body
                            Vector3 bodyPosAtRef = MainBody.orbit.getTruePositionAtUT(time);
                            Vector3 bodyPosNow = MainBody.orbit.getTruePositionAtUT(Planetarium.GetUniversalTime());
                            return bodyPosNow + (OrbitRender.driver.orbit.getTruePositionAtUT(time) - bodyPosAtRef);
                        }
                        else
                        {
                            //Vessel is probably orbiting the sun
                            return OrbitRender.driver.orbit.getTruePositionAtUT(time);
                        }

                    }
                }
                else
                    return LocalPosition;
            }
        }

        public Vector3 WorldVelocity
        {
            private set;
            get;
        }

        public CelestialBody MainBody
        {
            private set;
            get;
        }

        public GameObject GameObj
        {
            private set;
            get;
        }

        public LineRenderer Arc
        {
            private set;
            get;
        }

        public OrbitRenderer OrbitRender
        {
            private set;
            get;
        }

        public Color ActiveColor
        {
            private set;
            get;
        }

        public bool OrbitValid
        {
            private set;
            get;
        }

        public bool ShouldShowOrbit
        {
            get
            {
                if (!OrbitValid || SituationIsGrounded(Info.Situation))
                    return false;
                else
                    return (Info.State == State.Active && KLFGlobalSettings.Instance.ShowOrbits) || OrbitRender.mouseOver;
            }
        }

        public double ReferenceUT
        {
            private set;
            get;
        }

        public double ReferenceFixedTime
        {
            private set;
            get;
        }

        public double AdjustedUT
        {
            get
            {
                return ReferenceUT + (UnityEngine.Time.fixedTime - ReferenceFixedTime) * Info.TimeScale;
            }
        }

        //Methods
        public KLFVessel(String vesselName, String uName, Guid gid)
        {
            Info = new KLFVesselInfo();
            VesselName = vesselName;
            UserName = uName;
            Id = gid;

            //Build the name of the game object
            System.Text.StringBuilder sb = new StringBuilder();
            sb.Append(VesselName);
            sb.Append(" (");
            sb.Append(UserName);
            sb.Append(')');

            GameObj = new GameObject(sb.ToString());
            GameObj.layer = 9;

            GenerateActiveColor();

            Arc = GameObj.AddComponent<LineRenderer>();
            OrbitRender = GameObj.AddComponent<OrbitRenderer>();
            OrbitRender.driver = new OrbitDriver();

            Arc.transform.parent = GameObj.transform;
            Arc.transform.localPosition = Vector3.zero;
            Arc.transform.localEulerAngles = Vector3.zero;

            Arc.useWorldSpace = true;
            Arc.material = new Material(Shader.Find("Particles/Alpha Blended Premultiply"));
            Arc.SetVertexCount(2);
            Arc.enabled = false;

            MainBody = null;

            LocalDirection = Vector3.zero;
            LocalVelocity = Vector3.zero;
            LocalPosition = Vector3.zero;

            WorldDirection = Vector3.zero;
            WorldVelocity = Vector3.zero;

        }

        ~KLFVessel()
        {
        }

        public void GenerateActiveColor()
        {
            //Generate a display color from the owner name
            ActiveColor = GenerateActiveColor(UserName);
        }

        public static Color GenerateActiveColor(String str)
        {
            int val = 5381;
            foreach (char c in str)
                val = ((val << 5) + val) + c;
            return GenerateActiveColor(Math.Abs(val));
        }

        public static Color GenerateActiveColor(int seed)
        {
            //default high-passes:  saturation and value
            return ControlledColor(seed, (float)0.45, (float)0.45);
        }

        /* ControlledColor - return RGBA Color Obj from a string seed.
            * - alpha: always opaque
            * - hue, full spectrum, uniform distribution
            * - saturation, high-pass parameter, sigmoidal distribution
            *   * Needs to be adjusted depending on hue selected (blue,indigo,purple)
            * - value, high-pass parameter
            * - retrigger random value between h, s, and v to reduce correlations
            */
        public static Color ControlledColor(int seed, float sBand, float vBand)
        {
            float h;
            //deterministic random (same colour for same seed)
            System.Random r = new System.Random(seed);

            //Hue:  uniform distribution
            h = (float)r.NextDouble() * 360.0f;

            return ColorFromHSV(h, 0.85f, 1.0f);
        }

        /* ColorFromHSV - converts HSV to RGBA (UnityEngine)
            * - HSV designed by Palo Alto Research Center Incorporated
            *   and New York Institute of Technologies
            * - Formally described by Alvy Ray Smith, 1978.
            *   * http://en.wikipedia.org/wiki/HSL_and_HSV
            * - sample implementations:
            *   http://www.cs.rit.edu/~ncs/color/t_convert.html
            *   http://stackoverflow.com/a/1626175
            * - not implementing achromatic check optimization.
            *   We prevent dull input anyway. :)
            *
            */
        public static Color ColorFromHSV(float hue, float saturation, float lumValue)
        {
            //select colour sector (from degrees to 6 facets)
            int hSector = ((int)Math.Floor(hue / 60)) % 6;
            //select minor degree component within sector
            float hMinor = hue / 60f - (float)Math.Floor(hue / 60);

            //map HSV components to RGB
            float v = lumValue;
            float p = lumValue * (1f - saturation);
            float q = lumValue * (1f - saturation * hMinor);
            float t = lumValue * (1f - saturation * (1f - hMinor));

            //transpose RGB components based on hue sector
            if (hSector == 0)
                return new Color(v, t, p, 1f);
            else if (hSector == 1)
                return new Color(q, v, p, 1f);
            else if (hSector == 2)
                return new Color(p, v, t, 1f);
            else if (hSector == 3)
                return new Color(p, q, v, 1f);
            else if (hSector == 4)
                return new Color(t, p, v, 1f);
            else
                return new Color(v, p, q, 1f);
        }

        public void SetOrbitalData(CelestialBody body, Vector3 localPos, Vector3 localVel, Vector3 localDir)
        {
            MainBody = body;
            if (MainBody != null)
            {
                LocalPosition = localPos;
                TranslationFromBody = MainBody.transform.TransformPoint(LocalPosition) - MainBody.transform.position;
                LocalDirection = localDir;
                LocalVelocity = localVel;
                OrbitValid = true;

                //Check for invalid values in the physics data
                if (!SituationIsGrounded(Info.Situation)
                && ((LocalPosition.x == 0.0f && LocalPosition.y == 0.0f && LocalPosition.z == 0.0f)
                    || (LocalVelocity.x == 0.0f && LocalVelocity.y == 0.0f && LocalVelocity.z == 0.0f)
                    || LocalPosition.magnitude > MainBody.sphereOfInfluence))
                {
                    OrbitValid = false;
                }

                for (int i = 0; i < 3; i++)
                {//3 axis
                    if(float.IsNaN(LocalPosition[i])
                    || float.IsInfinity(LocalPosition[i]))
                    {
                        OrbitValid = false;
                        break;
                    }
                    if(float.IsNaN(LocalDirection[i])
                    || float.IsInfinity(LocalDirection[i]))
                    {
                        OrbitValid = false;
                        break;
                    }
                    if(float.IsNaN(LocalVelocity[i])
                    || float.IsInfinity(LocalVelocity[i]))
                    {
                        OrbitValid = false;
                        break;
                    }
                }

                if (!OrbitValid)
                {
                    //Debug.Log("Orbit invalid: " + VesselName);
                    //Spoof some values so the game doesn't freak out
                    LocalPosition = new Vector3(1000.0f, 1000.0f, 1000.0f);
                    TranslationFromBody = LocalPosition;
                    LocalDirection = new Vector3(1.0f, 0.0f, 0.0f);
                    LocalVelocity = new Vector3(1000.0f, 0.0f, 0.0f);
                }

                //Calculate world-space properties
                WorldDirection = MainBody.transform.TransformDirection(LocalDirection);
                WorldVelocity = MainBody.transform.TransformDirection(LocalVelocity);
                //Update game object transform
                UpdateOrbitProperties();
                UpdatePosition();
            }
        }

        public void UpdatePosition()
        {
            if (!OrbitValid)
                return;
            GameObj.transform.localPosition = WorldPosition;
            Vector3 scaledPos = ScaledSpace.LocalToScaledSpace(WorldPosition);

            //Determine the scale of the arc so its thickness is constant from the map camera view
            float apparentSize = 0.01f;
            bool pointed = true;
            switch (Info.State)
            {
            case State.Active:
                apparentSize = 0.015f;
                pointed = true;
                break;
            case State.Inactive:
                apparentSize = 0.01f;
                pointed = true;
                break;
            case State.Dead:
                apparentSize = 0.01f;
                pointed = false;
                break;
            }

            float scale = (float)(apparentSize * Vector3.Distance(MapView.MapCamera.transform.position, scaledPos));
            //Set arc vertex positions
            Vector3 arcHalfDir = WorldDirection * (scale * ScaledSpace.ScaleFactor);

            if (pointed)
                Arc.SetWidth(scale, 0);
            else
            {
                Arc.SetWidth(scale, scale);
                arcHalfDir *= 0.5f;
            }

            Arc.SetPosition(0, ScaledSpace.LocalToScaledSpace(WorldPosition - arcHalfDir));
            Arc.SetPosition(1, ScaledSpace.LocalToScaledSpace(WorldPosition + arcHalfDir));

            if (!SituationIsGrounded(Info.Situation))
                OrbitRender.driver.orbit.UpdateFromUT(AdjustedUT);
        }

        public void UpdateOrbitProperties()
        {
            if (MainBody != null)
            {
                Vector3 orbitPos = TranslationFromBody;
                Vector3 orbitVel = WorldVelocity;

                //Swap y and z values for orbital position/velocities
                float temp = orbitPos.y;
                orbitPos.y = orbitPos.z;
                orbitPos.z = temp;
                temp = orbitVel.y;
                orbitVel.y = orbitVel.z;
                orbitVel.z = temp;

                //Update orbit
                OrbitRender.driver.orbit.UpdateFromStateVectors( orbitPos
                        , orbitVel
                        , MainBody
                        , Planetarium.GetUniversalTime());
                ReferenceUT = Planetarium.GetUniversalTime();
                ReferenceFixedTime = UnityEngine.Time.fixedTime;
            }
        }

        public void UpdateRenderProperties(bool forceHide = false)
        {
            Color color = ActiveColor;
            Arc.enabled = !forceHide && OrbitValid && GameObj != null && MapView.MapIsEnabled;

            OrbitRenderer.DrawMode drawMode = OrbitRenderer.DrawMode.OFF;
            if (GameObj != null && !forceHide && ShouldShowOrbit)
                drawMode = OrbitRenderer.DrawMode.REDRAW_AND_RECALCULATE;
            if (OrbitRender.drawMode != drawMode)
                OrbitRender.drawMode = drawMode;

            if (OrbitRender.mouseOver)
                color = Color.white; //Change arc color when moused over
            else
            {
                switch (Info.State)
                {
                case State.Active:
                    color = ActiveColor;
                    break;
                case State.Inactive:
                    color = ActiveColor * 0.75f;
                    color.a = 1;
                    break;
                case State.Dead:
                    color = ActiveColor * 0.5f;
                    break;
                }
            }

            Arc.SetColors(color, color);
            OrbitRender.orbitColor = color * 0.5f;

            if (forceHide || !OrbitValid)
                OrbitRender.drawIcons = OrbitRenderer.DrawIcons.NONE;
            else if (Info.State == State.Active && ShouldShowOrbit)
                OrbitRender.drawIcons = OrbitRenderer.DrawIcons.OBJ_PE_AP;
            else
                OrbitRender.drawIcons = OrbitRenderer.DrawIcons.OBJ;
        }

        public static bool SituationIsGrounded(Situation situation)
        {
            switch (situation)
            {
                case Situation.Landed:
                case Situation.Splashed:
                case Situation.Prelaunch:
                case Situation.Destroyed:
                case Situation.Unknown:
                    return true;
                default:
                    return false;
            }
        }
    }
}
