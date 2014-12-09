using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KLF
{
    public enum Activity
    {
        None,
        Aerobraking,
        Parachuting,
        Docking
    }

    public enum Situation
    {
        Unknown,
        Destroyed,
        Landed,
        Splashed,
        Prelaunch,
        Orbiting,
        Encountering,
        Escaping,
        Ascending,
        Descending,
        Flying,
        Docked
    }

    public enum State
    {
        Active,
        Inactive,
        Dead
    }

    [Serializable()]
    public class KLFVesselDetail
    {
        /// <summary>
        /// The specific activity the vessel is performing in its situation
        /// </summary>
        public Activity Activity;

        /// <summary>
        /// Whether or not the player controlling this vessel is idle
        /// </summary>
        public bool Idle;

        /// <summary>
        /// The number of crew the vessel is holding. byte.Max signifies not applicable
        /// </summary>
        public byte CrewCount;

        /// <summary>
        /// The percentage of fuel remaining in the vessel. byte.Max signifies no fuel capacity
        /// </summary>
        public byte FuelPercent;

        /// <summary>
        /// The percentage of rcs fuel remaining in the vessel. byte.Max signifies no rcs capacity
        /// </summary>
        public byte RcsPercent;

        /// <summary>
        /// The mass of the vessel
        /// </summary>
        public float Mass;

        public KLFVesselDetail()
        {
            Activity = Activity.None;
            CrewCount = 0;
            FuelPercent = 0;
            RcsPercent = 0;
            Mass = 0.0f;
            Idle = false;
        }
    }

    [Serializable()]
    public class KLFVesselInfo
    {
        /// <summary>
        /// The vessel's KSP Vessel situation
        /// </summary>
        public Situation Situation;

        /// <summary>
        /// The vessel's KSP vessel state
        /// </summary>
        public State State;

        /// <summary>
        /// The timescale at which the vessel is warping
        /// </summary>
        public float TimeScale;

        /// <summary>
        /// The name of the body the vessel is orbiting
        /// </summary>
        public String BodyName;

        public KLFVesselDetail Detail;

        public KLFVesselInfo()
        {
            Situation = Situation.Unknown;
            TimeScale = 1.0f;
            Detail = null;
        }
    }

    [Serializable()]
    public class KLFVesselUpdate : KLFVesselInfo
    {
        /// <summary>
        /// The vessel name
        /// </summary>
        public String Name;

        /// <summary>
        /// The player who owns this ship
        /// </summary>
        public String Player;

        /// <summary>
        /// The ID of the vessel
        /// </summary>
        public Guid Id;

        /// <summary>
        /// The position of the vessel relative to its parent body transform
        /// </summary>
        public float[] Position;

        /// <summary>
        /// The direction of the vessel relative to its parent body transform
        /// </summary>
        public float[] Direction;

        /// <summary>
        /// The velocity of the vessel relative to its parent body transform
        /// </summary>
        public float[] Velocity;

        public KLFVesselUpdate()
        {
            Position = new float[3];
            Direction = new float[3];
            Velocity = new float[3];
        }
    }
}
