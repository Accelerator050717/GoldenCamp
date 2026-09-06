using UnityEngine;

namespace GoldCamp.Runtime
{
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public sealed class M0Input : MonoBehaviour
    {
        public bool IsPaused { get; private set; }
        public Vector2 Move { get; private set; }
        public Vector2 Look { get; private set; }
        public bool JumpPressed { get; private set; }
        public bool InteractPressed { get; private set; }
        public bool ThrowPressed { get; private set; }
        public bool ResetPressed { get; private set; }

        private void Start()
        {
            ApplyCursor();
        }

        private void Update()
        {
            ClearFrame();
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                SetPaused(!IsPaused);

            if (IsPaused)
                return;

            float horizontal = (UnityEngine.Input.GetKey(KeyCode.D) ? 1 : 0)
                             - (UnityEngine.Input.GetKey(KeyCode.A) ? 1 : 0);
            float vertical = (UnityEngine.Input.GetKey(KeyCode.W) ? 1 : 0)
                           - (UnityEngine.Input.GetKey(KeyCode.S) ? 1 : 0);
            Move = Vector2.ClampMagnitude(new Vector2(horizontal, vertical), 1f);
            Look = new Vector2(UnityEngine.Input.GetAxisRaw("Mouse X"), UnityEngine.Input.GetAxisRaw("Mouse Y"));
            JumpPressed = UnityEngine.Input.GetKeyDown(KeyCode.Space);
            InteractPressed = UnityEngine.Input.GetKeyDown(KeyCode.E);
            ThrowPressed = UnityEngine.Input.GetKeyDown(KeyCode.Q);
            ResetPressed = UnityEngine.Input.GetKeyDown(KeyCode.R);
        }

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            ClearFrame();
            Time.timeScale = paused ? 0f : 1f;
            ApplyCursor();
        }

        private void ApplyCursor()
        {
            if (Application.isBatchMode)
                return;
            Cursor.lockState = IsPaused ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = IsPaused;
        }

        private void ClearFrame()
        {
            Move = Look = Vector2.zero;
            JumpPressed = InteractPressed = ThrowPressed = ResetPressed = false;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && !Application.isBatchMode)
                SetPaused(true);
        }

        private void OnDisable()
        {
            if (IsPaused)
                Time.timeScale = 1f;
            if (!Application.isBatchMode)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }
}
