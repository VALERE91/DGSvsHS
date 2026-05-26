using System.Collections.Generic;
using UnityEngine;

namespace DGSvsHS.Client.Views
{
    public sealed class SpriteViewPool
    {
        private readonly Transform _parent;
        private readonly Sprite _sprite;
        private readonly Color _tint;
        private readonly float _spriteWorldSize;
        private readonly List<GameObject> _pool = new List<GameObject>(64);
        private readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>(64);
        private int _liveCount;

        public SpriteViewPool(Transform parent, Sprite sprite, Color tint, float spriteWorldSize)
        {
            _parent = parent;
            _sprite = sprite;
            _tint = tint;
            _spriteWorldSize = spriteWorldSize;
        }
        
        public void Begin() => _liveCount = 0;
        
        public SpriteRenderer Rent(out Transform tr)
        {
            if (_liveCount >= _pool.Count) Grow();
            var go = _pool[_liveCount];
            if (!go.activeSelf) go.SetActive(true);
            var sr = _renderers[_liveCount];
            tr = go.transform;
            _liveCount++;
            return sr;
        }
        
        public void End()
        {
            for (int i = _liveCount; i < _pool.Count; i++)
                if (_pool[i].activeSelf) _pool[i].SetActive(false);
        }

        private void Grow()
        {
            var go = new GameObject("PooledSprite");
            go.transform.SetParent(_parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _sprite;
            sr.color = _tint;
            if (_sprite != null && _sprite.bounds.size.x > 0f)
            {
                float scale = _spriteWorldSize / _sprite.bounds.size.x;
                go.transform.localScale = new Vector3(scale, scale, 1f);
            }
            _pool.Add(go);
            _renderers.Add(sr);
        }
    }
}
