using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CleanUp
{
    /// <summary>
    /// 마우스 입력을 받아 레이캐스트로 청소 명령을 전달하는 컨트롤러입니다.
    /// </summary>
    public class CleaningController : MonoBehaviour
    {
        #region SerializeField 필드

        [Header("청소 설정")]
        [SerializeField, Range(0.002f, 0.5f), Tooltip("브러시 반경 (월드 단위, m)")]
        private float brushWorldRadius = 0.03f;

        [SerializeField, Range(0.05f, 3f), Tooltip("브러시가 한 번 지나갈 때 중심에서 제거되는 더러움 양 (1 이상이면 한 번에 거의 닦임)")]
        private float passStrength = 1.5f;

        [SerializeField, Range(0f, 10f), Tooltip("브러시를 멈추고 있을 때 중심에서 초당 제거되는 더러움 양")]
        private float cleanRate = 1f;

        [SerializeField, Range(0.05f, 1f), Tooltip("브러시를 찍는 간격 (브러시 반경 대비 비율, 낮을수록 부드러움)")]
        private float stampSpacing = 0.25f;

        [SerializeField, Range(1, 64), Tooltip("프레임당 최대 브러시 찍기 횟수")]
        private int maxStampsPerFrame = 32;

        [SerializeField, Tooltip("브러시 모양 텍스처 (알파 채널 사용, null이면 원형)")]
        private Texture2D brushTexture;

        [SerializeField, Range(1f, 10f), Tooltip("브러시 안에서 패턴이 반복되는 횟수 (높을수록 촘촘함)")]
        private float brushPatternTiling = 3f;

        [SerializeField, Range(0f, 1f), Tooltip("브러시 패턴 영향도 (0=패턴 무시하고 균일하게, 1=패턴 알파 그대로)")]
        private float brushPatternInfluence = 0.5f;

        [SerializeField, Tooltip("스탬프마다 브러시 패턴을 무작위로 회전 (줄무늬 틈 메우기)")]
        private bool randomizeRotation = true;

        [SerializeField, Tooltip("청소 레이캐스트 대상 레이어")]
        private LayerMask cleanableLayers = ~0;

        [Header("붓 속도")]
        [SerializeField, Min(0.001f), Tooltip("속도 곡선 X=1에 해당하는 빠른 붓 속도 (m/s)")]
        private float referenceSpeed = 1f;

        [SerializeField, Tooltip("붓 속도에 따른 강도 배율 곡선 (0=정지/느림, 1=기준 속도 이상). 느릴수록 많이, 빠를수록 적게 닦이도록 내려가는 곡선 권장")]
        private AnimationCurve speedFalloffCurve = new AnimationCurve(new Keyframe(0f, 1f, 0f, -2f), new Keyframe(0.3f, 0.3f), new Keyframe(1f, 0.05f, -0.1f, 0f));

        [SerializeField, Min(0.1f), Tooltip("속도 스무딩 (높을수록 즉각 반응)")]
        private float speedSmoothing = 10f;

        [Header("감쇠 곡선")]
        [SerializeField, Tooltip("거리에 따른 강도 감쇠 곡선 (0=가장자리, 1=중심)")]
        private AnimationCurve distanceFalloffCurve = new AnimationCurve(new Keyframe(0f, 0f, 0f, 2.5f), new Keyframe(0.4f, 1f, 0f, 0f), new Keyframe(1f, 1f));

        [SerializeField, Tooltip("각도에 따른 강도 감쇠 곡선 (0=표면과 평행, 1=정면)")]
        private AnimationCurve angleFalloffCurve = new AnimationCurve(new Keyframe(0f, 0f, 0f, 3f), new Keyframe(0.5f, 1f, 0f, 0f), new Keyframe(1f, 1f));

        [Header("디버그")]
        [SerializeField, Tooltip("히트 지점 시각화")]
        private bool showHitPoint = true;

        [SerializeField, Tooltip("씬에 배치된 히트 인디케이터 오브젝트")]
        private GameObject hitIndicator;

        #endregion

        #region Private 필드

        private const int FalloffLutSize = 128;

        private Camera _mainCamera;
        private Texture2D _falloffLut;
        private readonly List<BrushStamp> _stamps = new List<BrushStamp>();

        private DirtySurface _lastSurface;
        private bool _hasPreviousPosition;
        private Vector2 _lastScreenPosition;
        private Vector3 _lastStrokePoint;
        private Vector3 _lastHitPoint;
        private Vector3 _lastHitNormal;
        private float _smoothedSpeed;
        private bool _hasHit;

        #endregion

        #region Public 필드 및 프로퍼티

        /// <summary>
        /// 스무딩된 현재 붓 속도 (m/s)입니다.
        /// </summary>
        public float BrushSpeed => _smoothedSpeed;

        #endregion

        #region Unity 메소드

        private void Start()
        {
            _mainCamera = Camera.main;

            if (_mainCamera == null)
            {
                Debug.LogError("[CleaningController] 메인 카메라를 찾을 수 없습니다!");
            }

            if (hitIndicator != null)
            {
                hitIndicator.SetActive(false);
            }

            BakeFalloffLut();
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _falloffLut != null)
            {
                BakeFalloffLut();
            }
        }

        // 오브젝트 회전(ObjectViewController.Update) 이후에 처리
        private void LateUpdate()
        {
            if (_mainCamera == null || Mouse.current == null)
            {
                SetIndicatorActive(false);
                ResetStroke();
                return;
            }

            Vector2 screenPosition = Mouse.current.position.ReadValue();
            _hasHit = TryRaycast(screenPosition, out RaycastHit hit);
            UpdateHitIndicator(hit);

            // 우클릭(회전) 중에는 청소하지 않음
            bool isCleaning = Mouse.current.leftButton.isPressed && !Mouse.current.rightButton.isPressed;
            DirtySurface surface = _hasHit ? hit.collider.GetComponentInParent<DirtySurface>() : null;

            if (isCleaning && surface != null)
            {
                PerformCleaning(surface, hit, screenPosition);
            }
            else
            {
                ResetStroke();
            }
        }

        private void OnDestroy()
        {
            if (_falloffLut != null)
            {
                Destroy(_falloffLut);
            }
        }

        private void OnDrawGizmos()
        {
            if (!showHitPoint || !Application.isPlaying || !_hasHit)
            {
                return;
            }

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(_lastHitPoint, brushWorldRadius);

            Gizmos.color = Color.green;
            Gizmos.DrawRay(_lastHitPoint, _lastHitNormal * 0.1f);
        }

        #endregion

        #region 히트 인디케이터

        /// <summary>
        /// 히트 인디케이터 위치와 회전을 업데이트합니다.
        /// </summary>
        /// <param name="hit">현재 프레임 레이캐스트 결과</param>
        private void UpdateHitIndicator(RaycastHit hit)
        {
            if (!_hasHit)
            {
                SetIndicatorActive(false);
                return;
            }

            _lastHitPoint = hit.point;
            _lastHitNormal = hit.normal;

            if (hitIndicator == null)
            {
                return;
            }

            SetIndicatorActive(showHitPoint);

            // 큐브의 Y축이 법선 방향을 향하고, 하단 면이 표면에 닿도록 배치
            hitIndicator.transform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
            float halfHeight = hitIndicator.transform.localScale.y * 0.5f;
            hitIndicator.transform.position = hit.point + hit.normal * halfHeight;
        }

        /// <summary>
        /// 히트 인디케이터 활성 상태를 설정합니다.
        /// </summary>
        private void SetIndicatorActive(bool isActive)
        {
            if (hitIndicator != null && hitIndicator.activeSelf != isActive)
            {
                hitIndicator.SetActive(isActive);
            }
        }

        #endregion

        #region 청소 처리

        /// <summary>
        /// 현재 프레임의 청소를 수행합니다. 이전 위치와의 경로를 월드 공간에서 나눠 찍습니다.
        /// </summary>
        /// <param name="surface">청소 대상 표면</param>
        /// <param name="hit">현재 마우스 위치의 레이캐스트 결과</param>
        /// <param name="screenPosition">현재 마우스 스크린 좌표</param>
        private void PerformCleaning(DirtySurface surface, RaycastHit hit, Vector2 screenPosition)
        {
            float deltaTime = Time.deltaTime;
            bool isContinuing = _hasPreviousPosition && _lastSurface == surface;
            float frameDistance = isContinuing ? Vector3.Distance(_lastStrokePoint, hit.point) : 0f;

            UpdateBrushSpeed(isContinuing, frameDistance, deltaTime);

            float speedFactor = speedFalloffCurve.Evaluate(Mathf.Clamp01(_smoothedSpeed / referenceSpeed));
            float dwellStrength = cleanRate * deltaTime;

            _stamps.Clear();

            if (isContinuing && frameDistance > 0f)
            {
                float spacing = Mathf.Max(brushWorldRadius * stampSpacing, 0.0001f);
                int stampCount = Mathf.Clamp(Mathf.CeilToInt(frameDistance / spacing), 1, maxStampsPerFrame);

                // 이동량 기반: 한 지점은 지름(2r) 구간 동안 스탬프를 받으므로, 한 번 지나가면 중심이 passStrength만큼 닦임
                float stepDistance = frameDistance / stampCount;
                float moveStrength = passStrength * stepDistance / (2f * brushWorldRadius);
                float stampStrength = (moveStrength + dwellStrength / stampCount) * speedFactor;

                for (int i = 1; i < stampCount; i++)
                {
                    Vector2 stepPosition = Vector2.Lerp(_lastScreenPosition, screenPosition, (float)i / stampCount);
                    if (TryRaycast(stepPosition, out RaycastHit stepHit) && stepHit.collider == hit.collider)
                    {
                        _stamps.Add(new BrushStamp(stepHit.point, stepHit.normal, stampStrength, NextStampRotation()));
                    }
                }

                _stamps.Add(new BrushStamp(hit.point, hit.normal, stampStrength, NextStampRotation()));
            }
            else
            {
                _stamps.Add(new BrushStamp(hit.point, hit.normal, dwellStrength * speedFactor, NextStampRotation()));
            }

            surface.Paint(_stamps, brushWorldRadius, brushTexture, brushPatternTiling, brushPatternInfluence, _falloffLut, _mainCamera.transform.position);

            _lastSurface = surface;
            _lastScreenPosition = screenPosition;
            _lastStrokePoint = hit.point;
            _hasPreviousPosition = true;
        }

        /// <summary>
        /// 붓 이동 속도를 갱신합니다.
        /// </summary>
        /// <param name="isContinuing">이전 프레임에서 이어지는 스트로크인지 여부</param>
        /// <param name="frameDistance">이번 프레임 이동 거리 (월드 단위)</param>
        /// <param name="deltaTime">프레임 시간</param>
        private void UpdateBrushSpeed(bool isContinuing, float frameDistance, float deltaTime)
        {
            if (!isContinuing)
            {
                _smoothedSpeed = 0f;
                return;
            }

            float speed = frameDistance / Mathf.Max(deltaTime, 0.0001f);
            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, speed, 1f - Mathf.Exp(-speedSmoothing * deltaTime));
        }

        /// <summary>
        /// 다음 스탬프의 브러시 회전 각도(라디안)를 반환합니다.
        /// </summary>
        private float NextStampRotation()
        {
            return randomizeRotation ? Random.Range(0f, Mathf.PI * 2f) : 0f;
        }

        /// <summary>
        /// 스트로크 상태를 초기화합니다.
        /// </summary>
        private void ResetStroke()
        {
            _hasPreviousPosition = false;
            _lastSurface = null;
            _smoothedSpeed = 0f;
        }

        /// <summary>
        /// 스크린 좌표에서 청소 대상 레이어로 레이캐스트합니다.
        /// </summary>
        private bool TryRaycast(Vector2 screenPosition, out RaycastHit hit)
        {
            Ray ray = _mainCamera.ScreenPointToRay(screenPosition);
            return Physics.Raycast(ray, out hit, Mathf.Infinity, cleanableLayers, QueryTriggerInteraction.Ignore);
        }

        #endregion

        #region 감쇠 LUT

        /// <summary>
        /// 거리/각도 감쇠 곡선을 셰이더용 LUT 텍스처로 굽습니다. (R=거리, G=각도)
        /// </summary>
        private void BakeFalloffLut()
        {
            if (_falloffLut == null)
            {
                _falloffLut = new Texture2D(FalloffLutSize, 1, TextureFormat.RGBA32, false, true)
                {
                    name = "CleanFalloffLut",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
            }

            Color[] pixels = new Color[FalloffLutSize];
            for (int i = 0; i < FalloffLutSize; i++)
            {
                float t = i / (float)(FalloffLutSize - 1);
                pixels[i] = new Color(
                    Mathf.Clamp01(distanceFalloffCurve.Evaluate(t)),
                    Mathf.Clamp01(angleFalloffCurve.Evaluate(t)),
                    0f,
                    1f);
            }

            _falloffLut.SetPixels(pixels);
            _falloffLut.Apply(false);
        }

        #endregion
    }
}
