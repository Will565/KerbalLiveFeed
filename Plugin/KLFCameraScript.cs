using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KLF
{
    class KLFCameraScript : MonoBehaviour
    {
        public KLFManager Manager;
        public void OnPreRender()
        {
            if (Manager != null)
                Manager.UpdateVesselPositions();
        }
    }
}
