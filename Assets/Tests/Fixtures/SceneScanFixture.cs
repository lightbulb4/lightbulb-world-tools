using UnityEngine;
using UnityEngine.Events;

namespace Lightbulb.WorldTools.Tests
{
    public sealed class SceneScanFixture : MonoBehaviour
    {
        public Object[] References;
        public UnityEvent Event = new UnityEvent();
    }
}
