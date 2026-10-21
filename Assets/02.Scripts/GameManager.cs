using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using TMPro;

namespace CleanUp
{
    /// <summary>
    /// 게임의 진행 상태, 시간, UI를 관리하는 매니저 클래스입니다.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        #region SerializeField 필드

        [Header("게임 설정")]
        [SerializeField, Range(0.5f, 1f), Tooltip("완료 판정 기준 (0~1)")]
        private float targetCleanPercentage = 0.9f;

        [SerializeField, Tooltip("시간 제한 (0=무제한)")]
        private float timeLimit = 0f;

        [Header("UI 참조")]
        [SerializeField, Tooltip("진행도 슬라이더")]
        private Slider progressSlider;

        [SerializeField, Tooltip("진행도 텍스트")]
        private TMP_Text progressText;

        [SerializeField, Tooltip("타이머 텍스트")]
        private TMP_Text timerText;

        [SerializeField, Tooltip("완료 패널")]
        private GameObject completionPanel;

        [SerializeField, Tooltip("완료 메시지 텍스트")]
        private TMP_Text completionMessageText;

        [Header("자동 탐색")]
        [SerializeField, Tooltip("시작 시 자동으로 DirtySurface 찾기")]
        private bool autoFindSurfaces = true;

        #endregion

        #region Private 필드

        private List<DirtySurface> _dirtySurfaces = new List<DirtySurface>();
        private float _elapsedTime = 0f;
        private bool _gameCompleted = false;
        private bool _gameFailed = false;

        #endregion

        #region Public 필드 및 프로퍼티

        /// <summary>
        /// 전체 청소 진행도를 반환합니다. (0~1)
        /// </summary>
        public float OverallProgress { get; private set; }

        #endregion

        #region Unity 메소드

        private void Start()
        {
            if (autoFindSurfaces)
            {
                FindAllDirtySurfaces();
            }

            if (completionPanel != null)
            {
                completionPanel.SetActive(false);
            }

            // 초기 진행도 계산 후 UI 업데이트
            CalculateOverallProgress();
            UpdateUI();
        }

        private void Update()
        {
            if (_gameCompleted || _gameFailed)
            {
                return;
            }

            _elapsedTime += Time.deltaTime;

            if (timeLimit > 0 && _elapsedTime >= timeLimit)
            {
                OnGameFailed();
                return;
            }

            CalculateOverallProgress();

            if (OverallProgress >= targetCleanPercentage)
            {
                OnGameCompleted();
            }

            UpdateUI();
        }

        #endregion

        #region 표면 관리

        /// <summary>
        /// 씬의 모든 DirtySurface를 찾아 등록합니다.
        /// </summary>
        public void FindAllDirtySurfaces()
        {
            _dirtySurfaces.Clear();
            DirtySurface[] surfaces = FindObjectsByType<DirtySurface>(FindObjectsSortMode.None);

            foreach (var surface in surfaces)
            {
                RegisterSurface(surface);
            }

            Debug.Log($"[GameManager] {_dirtySurfaces.Count}개의 DirtySurface를 찾았습니다.");
        }

        /// <summary>
        /// DirtySurface를 등록합니다.
        /// </summary>
        /// <param name="surface">등록할 표면</param>
        public void RegisterSurface(DirtySurface surface)
        {
            if (!_dirtySurfaces.Contains(surface))
            {
                _dirtySurfaces.Add(surface);
            }
        }

        #endregion

        #region 진행도 계산

        /// <summary>
        /// 전체 청소 진행도를 계산합니다. 표면별 더러움 양(면적)으로 가중 평균합니다.
        /// </summary>
        private void CalculateOverallProgress()
        {
            float totalDirt = 0f;
            float currentDirt = 0f;

            foreach (var surface in _dirtySurfaces)
            {
                if (surface != null)
                {
                    totalDirt += surface.TotalDirt;
                    currentDirt += surface.CurrentDirt;
                }
            }

            if (totalDirt <= 0f)
            {
                OverallProgress = 0f;
                return;
            }

            OverallProgress = Mathf.Clamp01(1f - (currentDirt / totalDirt));
        }

        #endregion

        #region UI 업데이트

        /// <summary>
        /// UI를 업데이트합니다.
        /// </summary>
        private void UpdateUI()
        {
            if (progressSlider != null)
            {
                progressSlider.value = OverallProgress;
            }

            if (progressText != null)
            {
                progressText.text = $"청소 진행도: {OverallProgress * 100f:F1}%";
            }

            if (timerText != null)
            {
                if (timeLimit > 0)
                {
                    float remainingTime = Mathf.Max(0f, timeLimit - _elapsedTime);
                    int minutes = Mathf.FloorToInt(remainingTime / 60f);
                    int seconds = Mathf.FloorToInt(remainingTime % 60f);
                    timerText.text = $"남은 시간: {minutes:00}:{seconds:00}";
                }
                else
                {
                    int minutes = Mathf.FloorToInt(_elapsedTime / 60f);
                    int seconds = Mathf.FloorToInt(_elapsedTime % 60f);
                    timerText.text = $"경과 시간: {minutes:00}:{seconds:00}";
                }
            }
        }

        #endregion

        #region 게임 상태 처리

        /// <summary>
        /// 게임 완료 시 호출됩니다.
        /// </summary>
        private void OnGameCompleted()
        {
            if (_gameCompleted)
            {
                return;
            }

            _gameCompleted = true;

            Debug.Log($"[GameManager] 게임 완료! 시간: {_elapsedTime:F2}초, 진행도: {OverallProgress * 100f:F1}%");

            if (completionPanel != null)
            {
                completionPanel.SetActive(true);
            }

            if (completionMessageText != null)
            {
                int minutes = Mathf.FloorToInt(_elapsedTime / 60f);
                int seconds = Mathf.FloorToInt(_elapsedTime % 60f);
                completionMessageText.text = $"청소 완료!\n시간: {minutes:00}:{seconds:00}\n진행도: {OverallProgress * 100f:F1}%";
            }
        }

        /// <summary>
        /// 게임 실패 시 호출됩니다.
        /// </summary>
        private void OnGameFailed()
        {
            if (_gameFailed)
            {
                return;
            }

            _gameFailed = true;

            Debug.Log($"[GameManager] 시간 초과! 진행도: {OverallProgress * 100f:F1}%");

            if (completionPanel != null)
            {
                completionPanel.SetActive(true);
            }

            if (completionMessageText != null)
            {
                completionMessageText.text = $"시간 초과!\n진행도: {OverallProgress * 100f:F1}%\n목표: {targetCleanPercentage * 100f:F0}%";
            }
        }

        /// <summary>
        /// 게임을 재시작합니다.
        /// </summary>
        public void RestartGame()
        {
            foreach (var surface in _dirtySurfaces)
            {
                if (surface != null)
                {
                    surface.ResetDirt();
                }
            }

            _elapsedTime = 0f;
            _gameCompleted = false;
            _gameFailed = false;
            OverallProgress = 0f;

            if (completionPanel != null)
            {
                completionPanel.SetActive(false);
            }

            UpdateUI();
            Debug.Log("[GameManager] 게임 재시작");
        }

        /// <summary>
        /// 모든 표면을 청소합니다.
        /// </summary>
        public void CleanAllSurfaces()
        {
            foreach (var surface in _dirtySurfaces)
            {
                if (surface != null)
                {
                    surface.CleanAll();
                }
            }

            Debug.Log("[GameManager] 모든 표면 청소 완료");
        }

        #endregion

        #region UI 버튼 이벤트

        /// <summary>
        /// 재시작 버튼 클릭 이벤트 핸들러입니다.
        /// </summary>
        public void OnRestartButtonClick()
        {
            RestartGame();
        }

        /// <summary>
        /// 종료 버튼 클릭 이벤트 핸들러입니다.
        /// </summary>
        public void OnQuitButtonClick()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        #endregion
    }
}
