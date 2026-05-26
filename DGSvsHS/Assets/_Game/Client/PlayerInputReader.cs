using DGSvsHS.Gameplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DGSvsHS.Client
{
    public sealed class PlayerInputReader
    {
        private readonly Camera _camera;

        public PlayerInputReader(Camera camera) { _camera = camera; }
        
        public InputCmd Sample(uint tick, Vector2 localPlayerWorldPos)
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return InputCmd.Empty(tick);
            
            Vector2 move = Vector2.zero;
            if (kb.wKey.isPressed) move.y += 1f;
            if (kb.sKey.isPressed) move.y -= 1f;
            if (kb.dKey.isPressed) move.x += 1f;
            if (kb.aKey.isPressed) move.x -= 1f;
            
            Vector2 aim = Vector2.right;
            if (_camera != null)
            {
                Vector3 mouseScreen = mouse.position.ReadValue();
                mouseScreen.z = -_camera.transform.position.z;
                Vector3 mouseWorld = _camera.ScreenToWorldPoint(mouseScreen);
                Vector2 delta = (Vector2)mouseWorld - localPlayerWorldPos;
                if (delta.sqrMagnitude > 0.0001f) aim = delta.normalized;
            }
            
            var flags = InputFlags.None;
            if (mouse.leftButton.isPressed) flags |= InputFlags.Fire;

            return new InputCmd { Tick = tick, Move = move, Aim = aim, Flags = flags };
        }
    }
}
