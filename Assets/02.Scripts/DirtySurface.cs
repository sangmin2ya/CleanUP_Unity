using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CleanUp
{
    /// <summary>
    /// 브러시 한 번 찍기에 대한 정보입니다.
    /// </summary>
    public struct BrushStamp
    {
        public Vector3 position;
        public Vector3 normal;
        public float strength;
        public float rotation;

        public BrushStamp(Vector3 position, Vector3 normal, float strength, float rotation = 0f)
        {
            this.position = position;
            this.normal = normal;
            this.strength = strength;
            this.rotation = rotation;
        }
    }

    /// <summary>
    /// 오브젝트 표면의 더러움을 GPU 마스크로 관리하고 청소 로직을 처리하는 컴포넌트입니다.
    /// 브러시는 월드 공간 구 안의 표면을 닦으며, UV는 마스크 저장 위치로만 사용합니다.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    [RequireComponent(typeof(MeshFilter))]
    public class DirtySurface : MonoBehaviour
    {
        #region SerializeField 필드

        [Header("더러움 설정")]
        [SerializeField, Range(0f, 1f), Tooltip("초기 더러움 정도 (0=깨끗, 1=완전히 더러움)")]
        private float initialDirtAmount = 1.0f;

        [SerializeField, Tooltip("더러움 마스크 해상도")]
        private int maskResolution = 1024;

        [SerializeField, Tooltip("메쉬에 UV2가 있으면 마스크용으로 사용 (겹치지 않는 UV 필요, 모델 Import의 Generate Lightmap UVs)")]
        private bool useSecondaryUV = true;

        [SerializeField, Range(0.05f, 2f), Tooltip("진행도 갱신 주기 (초)")]
        private float progressUpdateInterval = 0.25f;

        [Header("참조")]
        [SerializeField, Tooltip("마스크 페인팅 셰이더 (Hidden/CleanUp/DirtPaint)")]
        private Shader paintShader;

        #endregion

        #region Private 필드

        private const string PaintShaderName = "Hidden/CleanUp/DirtPaint";
        private const int PaintPass = 0;
        private const int CoveragePass = 1;
        private const int DilatePass = 2;

        private static readonly int dirtMaskId = Shader.PropertyToID("_DirtMask");
        private static readonly int maskUVChannelId = Shader.PropertyToID("_MaskUVChannel");
        private static readonly int brushPosId = Shader.PropertyToID("_CleanBrushPosWS");
        private static readonly int brushNormalId = Shader.PropertyToID("_CleanBrushNormalWS");
        private static readonly int cameraPosId = Shader.PropertyToID("_CleanCameraPosWS");
        private static readonly int maskSizeId = Shader.PropertyToID("_CleanMaskSize");
        private static readonly int brushParamsId = Shader.PropertyToID("_CleanBrushParams");
        private static readonly int brushTexId = Shader.PropertyToID("_CleanBrushTex");
        private static readonly int falloffLutId = Shader.PropertyToID("_CleanFalloffLut");
        private static readonly int sourceTexId = Shader.PropertyToID("_CleanSourceTex");
        private static readonly int coverageTexId = Shader.PropertyToID("_CleanCoverageTex");
        private static float[] halfToFloatTable;

        private MeshRenderer _meshRenderer;
        private int _subMeshCount;
        private float _maskUVChannel;
        private Material _paintMaterial;
        private CommandBuffer _commandBuffer;
        private MaterialPropertyBlock _propertyBlock;

        // 페인팅 대상 / 표시용(딜레이트 적용) / 메쉬 커버리지
        private RenderTexture _maskTexture;
        private RenderTexture _displayTexture;
        private RenderTexture _coverageTexture;

        private int[] _coveredIndices;
        private ushort[] _readbackBuffer;
        private Texture2D _fallbackReadTexture; // 비동기 읽기를 지원하지 않을 때 재사용하는 읽기용 텍스처

        private float _totalDirt;
        private float _currentDirt;

        private bool _isInitialized;
        private bool _hasPendingChanges;
        private bool _isReadbackPending;
        private float _nextReadbackTime;
        private int _maskVersion;

        #endregion

        #region Public 필드 및 프로퍼티

        /// <summary>
        /// 청소 진행도를 반환합니다. (0=더러움, 1=깨끗함)
        /// </summary>
        public float CleanProgress
        {
            get
            {
                if (_totalDirt <= 0f)
                {
                    return 1f;
                }
                return Mathf.Clamp01(1f - (_currentDirt / _totalDirt));
            }
        }

        /// <summary>
        /// 초기 상태의 전체 더러움 양 (메쉬가 차지하는 텍셀 기준)입니다.
        /// </summary>
        public float TotalDirt => _totalDirt;

        /// <summary>
        /// 남아 있는 더러움 양입니다. GPU 읽기 주기만큼 지연될 수 있습니다.
        /// </summary>
        public float CurrentDirt => _currentDirt;

        #endregion

        #region Unity 메소드

        private void Awake()
        {
            Initialize();
        }

        private void Update()
        {
            if (!_isInitialized || !_hasPendingChanges || _isReadbackPending)
            {
                return;
            }

            if (Time.unscaledTime < _nextReadbackTime)
            {
                return;
            }

            _hasPendingChanges = false;
            _nextReadbackTime = Time.unscaledTime + progressUpdateInterval;
            RequestProgressReadback();
        }

        private void OnDestroy()
        {
            _isInitialized = false;
            ReleaseTexture(ref _maskTexture);
            ReleaseTexture(ref _displayTexture);
            ReleaseTexture(ref _coverageTexture);

            if (_paintMaterial != null)
            {
                Destroy(_paintMaterial);
            }

            if (_fallbackReadTexture != null)
            {
                Destroy(_fallbackReadTexture);
            }

            _commandBuffer?.Release();
        }

        #endregion

        #region 초기화

        /// <summary>
        /// 마스크 텍스처, 페인트 머티리얼, 커버리지를 초기화합니다.
        /// </summary>
        private void Initialize()
        {
            _meshRenderer = GetComponent<MeshRenderer>();
            Mesh mesh = GetComponent<MeshFilter>().sharedMesh;

            if (mesh == null)
            {
                Debug.LogError($"[DirtySurface] {name}: MeshFilter에 메쉬가 없습니다.", this);
                return;
            }

            if (paintShader == null)
            {
                paintShader = Shader.Find(PaintShaderName);
            }

            if (paintShader == null)
            {
                Debug.LogError($"[DirtySurface] {name}: 페인트 셰이더({PaintShaderName})를 찾을 수 없습니다.", this);
                return;
            }

            _subMeshCount = mesh.subMeshCount;
            _maskUVChannel = SelectMaskUVChannel(mesh);

            _paintMaterial = new Material(paintShader);
            _paintMaterial.SetFloat(maskUVChannelId, _maskUVChannel);

            _commandBuffer = new CommandBuffer { name = "DirtySurface Paint" };

            _maskTexture = CreateMaskTexture("DirtMask");
            _displayTexture = CreateMaskTexture("DirtMaskDisplay");
            _coverageTexture = CreateMaskTexture("DirtMaskCoverage");
            _readbackBuffer = new ushort[maskResolution * maskResolution];

            SetupMaterial();
            BuildCoverage();
            FillMask(initialDirtAmount);

            _totalDirt = initialDirtAmount * _coveredIndices.Length;
            _currentDirt = _totalDirt;
            _isInitialized = true;
        }

        /// <summary>
        /// 마스크에 사용할 UV 채널을 결정합니다.
        /// </summary>
        /// <param name="mesh">대상 메쉬</param>
        /// <returns>0이면 UV0, 1이면 UV1</returns>
        private float SelectMaskUVChannel(Mesh mesh)
        {
            if (!useSecondaryUV)
            {
                return 0f;
            }

            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1))
            {
                return 1f;
            }

            Debug.LogWarning($"[DirtySurface] {name}: 메쉬에 UV2가 없어 UV0를 사용합니다. " +
                             "UV가 겹치면 다른 부위가 함께 닦이니 모델 Import 설정에서 Generate Lightmap UVs를 켜세요.", this);
            return 0f;
        }

        /// <summary>
        /// 마스크용 RenderTexture를 생성합니다.
        /// </summary>
        /// <param name="textureName">텍스처 이름</param>
        private RenderTexture CreateMaskTexture(string textureName)
        {
            RenderTexture texture = new RenderTexture(maskResolution, maskResolution, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
            {
                name = textureName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false
            };
            texture.Create();
            return texture;
        }

        /// <summary>
        /// 렌더러에 표시용 마스크와 UV 채널을 연결합니다.
        /// </summary>
        private void SetupMaterial()
        {
            _propertyBlock = new MaterialPropertyBlock();
            _meshRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetTexture(dirtMaskId, _displayTexture);
            _propertyBlock.SetFloat(maskUVChannelId, _maskUVChannel);
            _meshRenderer.SetPropertyBlock(_propertyBlock);
        }

        /// <summary>
        /// 메쉬를 UV 공간에 펼쳐 그려서 실제로 사용되는 텍셀 목록을 만듭니다.
        /// </summary>
        private void BuildCoverage()
        {
            _commandBuffer.Clear();
            _commandBuffer.SetRenderTarget(_coverageTexture);
            _commandBuffer.ClearRenderTarget(false, true, Color.clear);
            DrawMesh(_commandBuffer, CoveragePass);
            Graphics.ExecuteCommandBuffer(_commandBuffer);

            ReadTextureImmediate(_coverageTexture);

            List<int> covered = new List<int>(_readbackBuffer.Length / 2);
            for (int i = 0; i < _readbackBuffer.Length; i++)
            {
                if (HalfToFloat(_readbackBuffer[i]) > 0.5f)
                {
                    covered.Add(i);
                }
            }
            _coveredIndices = covered.ToArray();

            if (_coveredIndices.Length == 0)
            {
                Debug.LogWarning($"[DirtySurface] {name}: 마스크 커버리지가 비어 있습니다. 메쉬 UV를 확인하세요.", this);
            }
        }

        /// <summary>
        /// 텍스처를 해제합니다.
        /// </summary>
        private static void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }

            texture.Release();
            Destroy(texture);
            texture = null;
        }

        #endregion

        #region 청소 관련

        /// <summary>
        /// 브러시 스탬프들을 마스크에 적용합니다. 한 프레임의 스탬프를 모아서 한 번에 호출하세요.
        /// </summary>
        /// <param name="stamps">찍을 브러시 스탬프 목록</param>
        /// <param name="radius">브러시 반경 (월드 단위)</param>
        /// <param name="brushTexture">브러시 모양 텍스처 (알파 사용, null이면 원형)</param>
        /// <param name="brushTiling">브러시 안에서 패턴이 반복되는 횟수</param>
        /// <param name="patternInfluence">브러시 패턴 영향도 (0=패턴 무시, 1=패턴 알파 그대로)</param>
        /// <param name="falloffLut">감쇠 LUT (R=거리, G=각도)</param>
        /// <param name="cameraPosition">시선 각도 계산용 카메라 위치</param>
        public void Paint(IReadOnlyList<BrushStamp> stamps, float radius, Texture brushTexture, float brushTiling, float patternInfluence, Texture falloffLut, Vector3 cameraPosition)
        {
            if (!_isInitialized || stamps.Count == 0 || radius <= 0f)
            {
                return;
            }

            _commandBuffer.Clear();
            _commandBuffer.SetRenderTarget(_maskTexture);
            _commandBuffer.SetGlobalTexture(brushTexId, brushTexture != null ? brushTexture : Texture2D.whiteTexture);
            _commandBuffer.SetGlobalTexture(falloffLutId, falloffLut != null ? falloffLut : Texture2D.whiteTexture);
            _commandBuffer.SetGlobalVector(cameraPosId, cameraPosition);

            for (int i = 0; i < stamps.Count; i++)
            {
                BrushStamp stamp = stamps[i];
                if (stamp.strength <= 0f)
                {
                    continue;
                }

                Vector3 normal = stamp.normal.normalized;
                _commandBuffer.SetGlobalVector(brushPosId, new Vector4(stamp.position.x, stamp.position.y, stamp.position.z, radius));
                _commandBuffer.SetGlobalVector(brushNormalId, new Vector4(normal.x, normal.y, normal.z, stamp.strength));
                _commandBuffer.SetGlobalVector(brushParamsId, new Vector4(brushTiling, stamp.rotation, patternInfluence, 0f));
                DrawMesh(_commandBuffer, PaintPass);
            }

            AppendDilate(_commandBuffer);
            Graphics.ExecuteCommandBuffer(_commandBuffer);
            _hasPendingChanges = true;
        }

        /// <summary>
        /// 더러움을 초기 상태로 복원합니다.
        /// </summary>
        public void ResetDirt()
        {
            if (!_isInitialized)
            {
                return;
            }

            FillMask(initialDirtAmount);
            _currentDirt = _totalDirt;
        }

        /// <summary>
        /// 모든 더러움을 제거합니다.
        /// </summary>
        public void CleanAll()
        {
            if (!_isInitialized)
            {
                return;
            }

            FillMask(0f);
            _currentDirt = 0f;
        }

        /// <summary>
        /// 마스크 전체를 지정한 값으로 채우고, 진행 중인 읽기 결과를 무효화합니다.
        /// </summary>
        /// <param name="value">채울 더러움 값</param>
        private void FillMask(float value)
        {
            Color color = new Color(value, value, value, value);

            _commandBuffer.Clear();
            _commandBuffer.SetRenderTarget(_maskTexture);
            _commandBuffer.ClearRenderTarget(false, true, color);
            _commandBuffer.SetRenderTarget(_displayTexture);
            _commandBuffer.ClearRenderTarget(false, true, color);
            Graphics.ExecuteCommandBuffer(_commandBuffer);

            _maskVersion++;
            _hasPendingChanges = false;
        }

        /// <summary>
        /// 모든 서브메쉬를 지정한 패스로 그리는 명령을 추가합니다.
        /// </summary>
        private void DrawMesh(CommandBuffer commandBuffer, int pass)
        {
            for (int subMesh = 0; subMesh < _subMeshCount; subMesh++)
            {
                commandBuffer.DrawRenderer(_meshRenderer, _paintMaterial, subMesh, pass);
            }
        }

        /// <summary>
        /// 페인팅 결과를 UV 섬 바깥으로 확장해 표시용 마스크에 복사하는 명령을 추가합니다.
        /// </summary>
        private void AppendDilate(CommandBuffer commandBuffer)
        {
            commandBuffer.SetGlobalTexture(sourceTexId, _maskTexture);
            commandBuffer.SetGlobalTexture(coverageTexId, _coverageTexture);
            commandBuffer.SetGlobalVector(maskSizeId, new Vector4(maskResolution, maskResolution, 0f, 0f));
            commandBuffer.SetRenderTarget(_displayTexture);
            commandBuffer.DrawProcedural(Matrix4x4.identity, _paintMaterial, DilatePass, MeshTopology.Triangles, 3);
        }

        #endregion

        #region 진행도 계산

        /// <summary>
        /// 마스크를 GPU에서 읽어 남은 더러움 양을 갱신하도록 요청합니다.
        /// </summary>
        private void RequestProgressReadback()
        {
            int version = _maskVersion;

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                ReadTextureImmediate(_maskTexture);
                ApplyReadback(version);
                return;
            }

            _isReadbackPending = true;
            AsyncGPUReadback.Request(_maskTexture, 0, request =>
            {
                _isReadbackPending = false;

                if (!_isInitialized || request.hasError)
                {
                    return;
                }

                request.GetData<ushort>().CopyTo(_readbackBuffer);
                ApplyReadback(version);
            });
        }

        /// <summary>
        /// 읽어온 마스크 데이터로 남은 더러움 양을 계산합니다.
        /// </summary>
        /// <param name="version">요청 시점의 마스크 버전 (리셋 이후 도착한 결과는 무시)</param>
        private void ApplyReadback(int version)
        {
            if (version != _maskVersion)
            {
                return;
            }

            float dirt = 0f;
            for (int i = 0; i < _coveredIndices.Length; i++)
            {
                dirt += Mathf.Max(0f, HalfToFloat(_readbackBuffer[_coveredIndices[i]]));
            }

            _currentDirt = Mathf.Clamp(dirt, 0f, _totalDirt);
        }

        /// <summary>
        /// RenderTexture를 즉시 읽어 읽기 버퍼에 복사합니다.
        /// </summary>
        private void ReadTextureImmediate(RenderTexture source)
        {
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(source);
                request.WaitForCompletion();

                if (!request.hasError)
                {
                    request.GetData<ushort>().CopyTo(_readbackBuffer);
                    return;
                }
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;

            if (_fallbackReadTexture == null)
            {
                _fallbackReadTexture = new Texture2D(source.width, source.height, TextureFormat.RHalf, false, true);
            }

            _fallbackReadTexture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            RenderTexture.active = previous;

            _fallbackReadTexture.GetPixelData<ushort>(0).CopyTo(_readbackBuffer);
        }

        /// <summary>
        /// half(16비트) 값을 float로 변환합니다. (조회 테이블 사용)
        /// </summary>
        private static float HalfToFloat(ushort value)
        {
            if (halfToFloatTable == null)
            {
                halfToFloatTable = new float[ushort.MaxValue + 1];
                for (int i = 0; i <= ushort.MaxValue; i++)
                {
                    halfToFloatTable[i] = Mathf.HalfToFloat((ushort)i);
                }
            }

            return halfToFloatTable[value];
        }

        #endregion
    }
}
