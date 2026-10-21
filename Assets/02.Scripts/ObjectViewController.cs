using UnityEngine;
using UnityEngine.InputSystem;

namespace CleanUp
{
    /// <summary>
    /// 우클릭 드래그로 오브젝트를 회전하고, 휠로 Y축 위치를 조정하는 컨트롤러입니다.
    /// </summary>
    public class ObjectViewController : MonoBehaviour
    {
        #region SerializeField 필드

        [Header("대상")]
        [SerializeField, Tooltip("조작할 오브젝트의 트랜스폼")]
        private Transform targetObject;

        [Header("회전")]
        [SerializeField, Tooltip("회전 속도")]
        private float rotationSpeed = 1.0f;

        [Header("Y 위치")]
        [SerializeField, Tooltip("Y축 이동 속도")]
        private float yMovementSpeed = 1.0f;

        [SerializeField, Tooltip("최소 Y 위치")]
        private float minY = -5f;

        [SerializeField, Tooltip("최대 Y 위치")]
        private float maxY = 5f;

        #endregion

        #region Private 필드

        private Vector2 _lastMousePosition;

        #endregion

        #region Public 필드 및 프로퍼티

        /// <summary>
        /// 조작 대상 오브젝트를 가져오거나 설정합니다.
        /// </summary>
        public Transform TargetObject
        {
            get => targetObject;
            set => targetObject = value;
        }

        #endregion

        #region Unity 메소드

        private void Start()
        {
            if (targetObject == null)
            {
                DirtySurface surface = FindFirstObjectByType<DirtySurface>();
                if (surface != null)
                {
                    targetObject = surface.transform;
                }
            }
        }

        private void Update()
        {
            if (targetObject == null || Mouse.current == null)
            {
                return;
            }

            HandleRotation();
            HandleYMovement();
        }

        #endregion

        #region 입력 처리

        /// <summary>
        /// 우클릭 드래그 회전을 처리합니다.
        /// </summary>
        private void HandleRotation()
        {
            if (Mouse.current.rightButton.wasPressedThisFrame)
            {
                _lastMousePosition = Mouse.current.position.ReadValue();
            }

            if (Mouse.current.rightButton.isPressed)
            {
                Vector2 currentMouse = Mouse.current.position.ReadValue();
                Vector2 delta = currentMouse - _lastMousePosition;

                targetObject.Rotate(Vector3.forward, -delta.x * rotationSpeed, Space.World);
                targetObject.Rotate(Vector3.right, delta.y * rotationSpeed, Space.World);

                _lastMousePosition = currentMouse;
            }
        }

        /// <summary>
        /// 휠 스크롤 Y축 이동을 처리합니다.
        /// </summary>
        private void HandleYMovement()
        {
            float scrollValue = Mouse.current.scroll.y.ReadValue();
            if (scrollValue != 0)
            {
                float scroll = scrollValue / 120f;

                Vector3 pos = targetObject.position;
                pos.y += scroll * yMovementSpeed;
                pos.y = Mathf.Clamp(pos.y, minY, maxY);
                targetObject.position = pos;
            }
        }

        #endregion
    }
}
