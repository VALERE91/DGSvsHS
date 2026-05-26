using System.Collections.Generic;
using DGSvsHS.Gameplay;
using UnityEngine;

namespace DGSvsHS.Client.Views
{
    public sealed class BeamViewPool
    {
        public const float BeamLifetimeSec = 0.12f;
        public const float BeamWidth = 0.2f;
        public const int BeamSortingOrder = 1000;

        private readonly Transform _parent;
        private readonly Color _color;
        private readonly Material _material;

        private struct LiveBeam
        {
            public LineRenderer Renderer;
            public float DieAt;
        }

        private readonly List<LiveBeam> _live = new List<LiveBeam>(32);
        private readonly Stack<LineRenderer> _free = new Stack<LineRenderer>(32);

        public BeamViewPool(Transform parent, Color color)
        {
            _parent = parent;
            _color = color;
            _material = new Material(FindBestShader()) { color = color };
        }
        
        private static Shader FindBestShader()
        {
            string[] candidates =
            {
                "Universal Render Pipeline/2D/Sprite-Unlit-Default",
                "Universal Render Pipeline/Unlit",
                "Sprites/Default",
                "Unlit/Color",
                "Hidden/Internal-Colored",
            };
            foreach (var name in candidates)
            {
                var s = Shader.Find(name);
                if (s != null) return s;
            }
            return Shader.Find("Hidden/Internal-Colored");
        }

        public void Spawn(in FireEvent ev, float now)
        {
            LineRenderer lr;
            if (_free.Count > 0)
            {
                lr = _free.Pop();
                lr.gameObject.SetActive(true);
            }
            else
            {
                var go = new GameObject("Beam");
                go.transform.SetParent(_parent, false);
                lr = go.AddComponent<LineRenderer>();
                lr.material = _material;
                lr.positionCount = 2;
                lr.startWidth = BeamWidth;
                lr.endWidth = BeamWidth;
                lr.useWorldSpace = true;
                lr.numCapVertices = 2;
                lr.alignment = LineAlignment.View;
                lr.textureMode = LineTextureMode.Stretch;
                lr.startColor = _color;
                lr.endColor = new Color(_color.r, _color.g, _color.b, 0.4f);
                lr.sortingLayerID = SortingLayer.NameToID("Default");
                lr.sortingOrder = BeamSortingOrder;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                lr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }

            Vector2 origin = ev.Origin;
            Vector2 end = origin + ev.Direction * ev.Distance;
            lr.SetPosition(0, new Vector3(origin.x, origin.y, -0.1f));
            lr.SetPosition(1, new Vector3(end.x, end.y, -0.1f));

            _live.Add(new LiveBeam { Renderer = lr, DieAt = now + BeamLifetimeSec });
        }

        public void Tick(float now)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var b = _live[i];
                if (now >= b.DieAt)
                {
                    b.Renderer.gameObject.SetActive(false);
                    _free.Push(b.Renderer);
                    _live.RemoveAt(i);
                }
            }
        }
    }
}
