using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools.Tests
{
    public sealed class SceneReferenceScanTests
    {
        private static void ReadValue(object value, Action<Object, string> reference)
        {
            typeof(SceneReferenceScan).GetMethod("ReadUdonValue", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { value, "Udon variable: Values", reference, new HashSet<object>() });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnimationCurvesAllowLaterObjectReferences(bool curveArray)
        {
            var curve = AnimationCurve.Linear(0, 0, 1, 1);
            curve.preWrapMode = WrapMode.Loop;
            curve.postWrapMode = WrapMode.PingPong;
            var keys = curve.keys;
            var texture = new Texture2D(1, 1);
            try
            {
                var references = new List<Object>();
                var paths = new List<string>();
                object curveValue = curveArray ? (object)new[] { curve } : curve;
                ReadValue(new[] { curveValue, texture }, (value, path) => { references.Add(value); paths.Add(path); });
                Assert.That(references, Is.EqualTo(new Object[] { texture }));
                Assert.That(paths, Is.EqualTo(new[] { "Udon variable: Values[1]" }));
                Assert.That(curve.keys, Is.EqualTo(keys));
                Assert.That(curve.preWrapMode, Is.EqualTo(WrapMode.Loop));
                Assert.That(curve.postWrapMode, Is.EqualTo(WrapMode.PingPong));
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void UnknownReferenceContainersStillBlockScan()
        {
            var exception = Assert.Throws<TargetInvocationException>(() =>
                ReadValue(new List<Object>(), (_, __) => { }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException.Message, Does.Contain("Unsupported Udon reference container at Udon variable: Values"));
        }
    }
}
