using UnityEngine;

namespace GoldCamp.Runtime
{
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(CharacterController), typeof(M0Input))]
    [DisallowMultipleComponent]
    public sealed class FirstPersonController : MonoBehaviour
    {
        public Camera View;
        public Transform HoldPoint;
        public CarryableOre ResetOre;
        public Vector3 SpawnPosition = new Vector3(0f, 1f, 0f);
        public float MoveSpeed = 4.5f;
        public float MouseSensitivity = 2f;
        public float JumpHeight = 1.1f;
        public float Gravity = -22f;
        public float RescueHeight = -12f;

        private CharacterController _controller;
        private M0Input _input;
        private float _verticalSpeed;
        private float _pitch;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<M0Input>();
        }

        private void Update()
        {
            if (transform.position.y < RescueHeight || _input.ResetPressed)
            {
                ResetPlayer();
                return;
            }
            if (_input.IsPaused || !_controller.enabled)
                return;

            transform.Rotate(0f, _input.Look.x * MouseSensitivity, 0f, Space.Self);
            _pitch = Mathf.Clamp(_pitch - _input.Look.y * MouseSensitivity, -80f, 80f);
            if (View != null)
                View.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            if (_controller.isGrounded && _verticalSpeed < 0f)
                _verticalSpeed = -2f;
            if (_controller.isGrounded && _input.JumpPressed)
                _verticalSpeed = Mathf.Sqrt(Mathf.Max(0f, JumpHeight * -2f * Gravity));
            _verticalSpeed += Gravity * Time.deltaTime;

            Vector3 horizontal = (transform.right * _input.Move.x + transform.forward * _input.Move.y) * MoveSpeed;
            _controller.Move((horizontal + Vector3.up * _verticalSpeed) * Time.deltaTime);
        }

        public void ResetPlayer()
        {
            if (_controller == null)
                _controller = GetComponent<CharacterController>();
            OreCarrier carrier = GetComponent<OreCarrier>();
            if (carrier != null)
                carrier.ResetHeldOre();
            if (ResetOre != null)
                ResetOre.ResetToSpawn();
            bool wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.SetPositionAndRotation(SpawnPosition, Quaternion.identity);
            _controller.enabled = wasEnabled;
            _verticalSpeed = 0f;
            _pitch = 0f;
            if (View != null)
                View.transform.localRotation = Quaternion.identity;
            Physics.SyncTransforms();
        }
    }
}
