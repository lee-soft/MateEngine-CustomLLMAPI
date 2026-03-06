using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace CustomLLMAPI
{
    /// <summary>
    /// PuppetMasterActions — pure avatar control surface.
    ///
    /// This class contains no HTTP, socket, or serialisation concerns.
    /// Every public method is safe to call directly from the LLM integration
    /// (or from the HTTP layer) after being dispatched onto the Unity main thread.
    ///
    /// All methods must be called on the Unity main thread (they touch GameObjects).
    /// Use <see cref="PuppetMaster.EnqueueOnMainThread"/> when calling from a
    /// background thread (e.g. from the HTTP server).
    /// </summary>
    public class PuppetMasterActions : MonoBehaviour
    {
        // ── Shared state (read from any thread, written only on main thread) ──

        public string CurrentMood { get; private set; } = "Neutral";
        public bool IsDancing { get; private set; } = false;
        public bool IsWalking { get; private set; } = false;
        public bool IsBigScreen { get; private set; } = false;
        public float AvatarSize { get; private set; } = 1.0f;

        private Coroutine _moodHoldCoroutine;
        private PuppetMasterMoodProfile _moodProfile;

        // ── Initialisation ────────────────────────────────────────────────────

        public void Initialise()
        {
            _moodProfile = PuppetMasterMoodProfile.Load();
        }

        // ── Status ────────────────────────────────────────────────────────────

        public AvatarStatus GetStatus()
        {
            bool isWindowSit = false;
            string snappedTitle = "";
            var handler = FindAnyObjectByType<AvatarWindowHandler>();
            if (handler != null)
            {
                var priv = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var type = typeof(AvatarWindowHandler);
                var snappedHWND = (IntPtr)(type.GetField("snappedHWND", priv)?.GetValue(handler) ?? IntPtr.Zero);
                if (snappedHWND != IntPtr.Zero)
                {
                    isWindowSit = true;
                    var sb = new StringBuilder(256);
                    GetWindowText(snappedHWND, sb, sb.Capacity);
                    snappedTitle = sb.ToString();
                }
            }
            return new AvatarStatus
            {
                mood = CurrentMood,
                dancing = IsDancing,
                walking = IsWalking,
                bigscreen = IsBigScreen,
                avatar = GetAvatarDisplayName(),
                size = AvatarSize > 0f ? AvatarSize : (SaveLoadHandler.Instance?.data?.avatarSize ?? 1f),
                isWindowSit = isWindowSit,
                snappedWindowTitle = snappedTitle
            };
        }

        // ── Avatar Size ───────────────────────────────────────────────────────

        /// <summary>Returns the current avatar scale (live, mid-tween accurate).</summary>
        public float GetAvatarSize()
        {
            if (AvatarSize > 0f) return AvatarSize;
            float saved = SaveLoadHandler.Instance?.data?.avatarSize ?? 1f;
            AvatarSize = saved;
            return saved;
        }

        /// <summary>
        /// Smoothly interpolates the avatar scale to <paramref name="size"/> over
        /// <paramref name="duration"/> seconds, then persists the final value.
        /// Safe to call from any thread — dispatches the coroutine onto the main thread.
        /// </summary>
        /// <param name="size">Target scale. Clamped to 0.5–1.3.</param>
        /// <param name="duration">Transition time in seconds (default 0.6s).</param>
        /// <returns>"ok" immediately (transition runs in background).</returns>
        public string SetAvatarSize(float size, float duration = 0.6f)
        {
            size = Mathf.Clamp(size, 0.5f, 1.3f);
            if (_sizeCoroutine != null) StopCoroutine(_sizeCoroutine);
            _sizeCoroutine = StartCoroutine(SmoothScale(size, duration));
            return "ok";
        }

        /// <summary>Sets avatar scale instantly with no tween. Used on startup / settings load.</summary>
        public string SetAvatarSizeImmediate(float size)
        {
            size = Mathf.Clamp(size, 0.5f, 1.3f);
            if (_sizeCoroutine != null) { StopCoroutine(_sizeCoroutine); _sizeCoroutine = null; }
            ApplyScale(size);
            return "ok";
        }

        private Coroutine _sizeCoroutine;

        private IEnumerator SmoothScale(float target, float duration)
        {
            float start = AvatarSize;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
                ApplyScale(Mathf.Lerp(start, target, t));
                yield return null;
            }

            ApplyScale(target);
            _sizeCoroutine = null;

            // Persist once the tween settles.
            if (SaveLoadHandler.Instance?.data != null)
            {
                SaveLoadHandler.Instance.data.avatarSize = target;
                SaveLoadHandler.Instance.SaveToDisk();
            }
        }

        private void ApplyScale(float size)
        {
            AvatarSize = size;
            var avatars = Resources.FindObjectsOfTypeAll<AvatarAnimatorController>();
            foreach (var avatar in avatars)
                if (avatar != null)
                    avatar.transform.localScale = Vector3.one * size;
        }

        // ── Mood ──────────────────────────────────────────────────────────────

        /// <summary>Sets the avatar's facial mood blendshapes and holds them every frame.</summary>
        /// <returns>"ok" or an error string.</returns>
        public string SetMood(string mood)
        {
            if (_moodHoldCoroutine != null) StopCoroutine(_moodHoldCoroutine);
            CurrentMood = mood;
            _moodHoldCoroutine = StartCoroutine(HoldMood(mood));
            return "ok";
        }

        /// <summary>Returns the list of mood names defined for the current avatar.</summary>
        public List<string> GetMoodList()
        {
            if (_moodProfile == null) return new List<string>();

            string avatarName = GetAvatarDisplayName();
            var profile = _moodProfile.GetProfileFor(avatarName);

            if (profile == null && _moodProfile.profiles.Count > 0)
            {
                profile = _moodProfile.profiles[0];
                Debug.LogWarning("[PuppetMasterActions] GetMoodList — no profile for '" + avatarName + "', falling back to: " + profile.avatarName);
            }

            if (profile == null) return new List<string>();
            return profile.moods.ConvertAll(m => m.name);
        }

        /// <summary>Reloads MoodProfile.json from disk.</summary>
        /// <returns>Number of moods loaded for the current avatar.</returns>
        public int ReloadMoodProfile()
        {
            _moodProfile = PuppetMasterMoodProfile.Load();
            var profile = _moodProfile.GetProfileFor(GetAvatarDisplayName());
            return profile?.moods.Count ?? 0;
        }

        // ── Dance ─────────────────────────────────────────────────────────────

        /// <summary>Starts a dance animation by clip index.</summary>
        public string StartDance(int index = 0)
        {
            var ctrl = FindAnyObjectByType<AvatarAnimatorController>();
            if (ctrl == null) return "ERROR: AvatarAnimatorController not found";

            ctrl.animator.SetFloat("DanceIndex", (float)index);
            ctrl.animator.SetBool("isDancing", true);
            IsDancing = true;
            return "ok";
        }

        /// <summary>Stops the current dance.</summary>
        public string StopDance()
        {
            var ctrl = FindAnyObjectByType<AvatarAnimatorController>();
            if (ctrl == null) return "ERROR: AvatarAnimatorController not found";

            ctrl.animator.SetBool("isDancing", false);
            IsDancing = false;
            return "ok";
        }

        // ── Walk ──────────────────────────────────────────────────────────────

        public string StartWalk()
        {
            var loco = FindAnyObjectByType<AvatarLocomotionController>();
            if (loco == null) return "ERROR: AvatarLocomotionController not found";

            loco.EnableLocomotion = true;
            IsWalking = true;
            return "ok";
        }

        public string StopWalk()
        {
            var loco = FindAnyObjectByType<AvatarLocomotionController>();
            if (loco == null) return "ERROR: AvatarLocomotionController not found";

            loco.EnableLocomotion = false;
            IsWalking = false;
            return "ok";
        }

        // ── Message ───────────────────────────────────────────────────────────

        /// <summary>Displays a speech bubble / Minecraft-style message above the avatar.</summary>
        public string ShowMessage(string text)
        {
            StartCoroutine(ShowMessageCoroutine(text));
            return "ok";
        }

        // ── Big Screen ────────────────────────────────────────────────────────

        /// <summary>Enables, disables, or toggles the big-screen overlay.</summary>
        /// <param name="active">Pass null to toggle.</param>
        public string SetBigScreen(bool? active)
        {
            var handler = FindAnyObjectByType<AvatarBigScreenHandler>();
            if (handler == null) return "ERROR: AvatarBigScreenHandler not found";

            if (active == null)
            {
                handler.ToggleBigScreenFromUI();
                IsBigScreen = !IsBigScreen;
            }
            else if (active.Value != IsBigScreen)
            {
                handler.ToggleBigScreenFromUI();
                IsBigScreen = active.Value;
            }
            return "ok";
        }

        // ── Animations ────────────────────────────────────────────────────────

        /// <summary>Returns all animator parameters that are safe to expose externally.</summary>
        public List<AnimatorParamInfo> GetAnimations()
        {
            var ctrl = FindAnyObjectByType<AvatarAnimatorController>();
            if (ctrl == null) return new List<AnimatorParamInfo>();

            var result = new List<AnimatorParamInfo>();
            var blocked = new HashSet<string> { "isDancing", "DanceIndex", "isDragging", "isIdle", "IdleIndex", "isMale", "isFemale" };

            foreach (var p in ctrl.animator.parameters)
            {
                if (blocked.Contains(p.name)) continue;
                result.Add(new AnimatorParamInfo { name = p.name, type = p.type.ToString() });
            }
            return result;
        }

        /// <summary>Sets an animator parameter by name.</summary>
        public string TriggerAnimation(string param, bool value)
        {
            var ctrl = FindAnyObjectByType<AvatarAnimatorController>();
            if (ctrl == null) return "ERROR: AvatarAnimatorController not found";
            if (string.IsNullOrEmpty(param)) return "ERROR: no param";

            var animator = ctrl.animator;
            foreach (var p in animator.parameters)
            {
                if (p.name != param) continue;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        animator.SetBool(param, value); break;
                    case AnimatorControllerParameterType.Trigger:
                        animator.SetTrigger(param); break;
                    case AnimatorControllerParameterType.Float:
                        animator.SetFloat(param, value ? 1f : 0f); break;
                    case AnimatorControllerParameterType.Int:
                        animator.SetInteger(Int32.Parse(param), value ? 1 : 0); break;
                }
                return "ok";
            }
            return "ERROR: param not found";
        }

        // ── Window Sit ───────────────────────────────────────────────────

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out WinRect rect);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect { public int Left, Top, Right, Bottom; }

        /// <summary>Returns visible, titled, reasonably-sized top-level window titles.</summary>
        public List<string> GetWindowList()
        {
            var result = new List<string>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;
                if (!GetWindowRect(hWnd, out WinRect r)) return true;
                if ((r.Right - r.Left) < 200 || (r.Bottom - r.Top) < 60) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (!string.IsNullOrEmpty(title))
                    result.Add(title);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>
        /// Returns only windows that are not minimized and not substantially occluded,
        /// tagged with [FOCUSED] or [VISIBLE] for LLM consumption.
        /// </summary>
        public List<string> GetVisibleWindowList()
        {
            var result = new List<string>();
            IntPtr foreground = GetForegroundWindow();

            var candidates = new List<(IntPtr hwnd, string title, WinRect rect)>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (IsIconic(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;
                if (!GetWindowRect(hWnd, out WinRect r)) return true;
                if ((r.Right - r.Left) < 200 || (r.Bottom - r.Top) < 60) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (!string.IsNullOrEmpty(title))
                    candidates.Add((hWnd, title, r));
                return true;
            }, IntPtr.Zero);

            foreach (var (hwnd, title, rect) in candidates)
            {
                if (IsSubstantiallyOccluded(hwnd, rect, candidates)) continue;
                string tag = hwnd == foreground ? "[FOCUSED]" : "[VISIBLE]";
                result.Add(tag + " " + title);
            }

            return result;
        }

        private bool IsSubstantiallyOccluded(IntPtr hwnd, WinRect rect,
            List<(IntPtr hwnd, string title, WinRect rect)> allWindows)
        {
            float totalArea = (float)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (totalArea <= 0) return true;

            float occludedArea = 0f;
            bool foundSelf = false;

            foreach (var (otherHwnd, _, otherRect) in allWindows)
            {
                if (otherHwnd == hwnd) { foundSelf = true; continue; }
                if (foundSelf) continue;

                int ix1 = Math.Max(rect.Left, otherRect.Left);
                int iy1 = Math.Max(rect.Top, otherRect.Top);
                int ix2 = Math.Min(rect.Right, otherRect.Right);
                int iy2 = Math.Min(rect.Bottom, otherRect.Bottom);

                if (ix2 > ix1 && iy2 > iy1)
                    occludedArea += (float)(ix2 - ix1) * (iy2 - iy1);
            }

            return (occludedArea / totalArea) > 0.70f;
        }

        /// <summary>Snaps the pet to sit on whichever window currently has focus.</summary>
        public string SnapToFocusedWindow()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "ERROR: no focused window";
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrEmpty(title)) return "ERROR: focused window has no title";
            return SnapToWindow(title);
        }

        /// <summary>Gets the pet off any window it is currently sitting on.</summary>
        public string Unsit()
        {
            var handler = FindAnyObjectByType<AvatarWindowHandler>();
            if (handler == null) return "ERROR: AvatarWindowHandler not found";
            handler.ForceExitWindowSitting();
            return "ok";
        }

        /// <summary>
        /// Snaps the pet to sit on the window matching <paramref name="windowTitle"/>.
        /// Enables window sitting in SaveData, then drives AvatarWindowHandler via reflection.
        /// </summary>
        public string SnapToWindow(string windowTitle)
        {
            if (string.IsNullOrEmpty(windowTitle)) return "ERROR: no title";

            IntPtr targetHwnd = FindWindow(null, windowTitle);
            if (targetHwnd == IntPtr.Zero) return "ERROR: window not found";
            if (!GetWindowRect(targetHwnd, out WinRect tr)) return "ERROR: could not get window rect";

            // Make sure the feature flag is on
            if (SaveLoadHandler.Instance?.data != null)
                SaveLoadHandler.Instance.data.enableWindowSitting = true;

            var handler = FindAnyObjectByType<AvatarWindowHandler>();
            if (handler == null) return "ERROR: AvatarWindowHandler not found";

            var type  = typeof(AvatarWindowHandler);
            var priv  = BindingFlags.NonPublic | BindingFlags.Instance;
            var pub   = BindingFlags.Public    | BindingFlags.Instance;

            var animatorField     = type.GetField("animator",              priv);
            var snappedField      = type.GetField("snappedHWND",           priv);
            var fracField         = type.GetField("snapFraction",          priv);
            var guardField        = type.GetField("_guard",                priv);
            var latchField        = type.GetField("_latch",                priv);
            var smoothField       = type.GetField("_snapSmoothingActive",  priv);
            var velXField         = type.GetField("_snapVelX",             priv);
            var velYField         = type.GetField("_snapVelY",             priv);
            var prevRectField     = type.GetField("_havePrevSnapRect",     priv);
            var lastSnapYField    = type.GetField("_lastSnapTopY",         priv);
            var recentUnsnapField = type.GetField("_recentUnsnap",         priv);
            var guardZoneField    = type.GetField("_guardZoneActive",      priv);
            var settleFrames      = type.GetField("_postSettleFrames",     priv);
            var settleRecalib     = type.GetField("_postSettleRecalib",    priv);

            var calibrate        = type.GetMethod("CalibrateSeatAnchorToDesktopY", priv);
            var setTopMost       = type.GetMethod("SetTopMost",                    priv);
            var rebuildOccluders = type.GetMethod("RebuildActiveOccluders",        priv);
            var updateOccluders  = type.GetMethod("UpdateOccluderQuadsFrameSync",  priv);
            var pinToTarget      = type.GetMethod("PinToTarget",                   priv);

            if (animatorField == null || snappedField == null)
                return "ERROR: reflection failed";

            var animator = animatorField.GetValue(handler) as Animator;
            if (animator == null) return "ERROR: animator not resolved";

            // Populate cachedWindows NOW so Update()'s validation check finds the window
            var updateCached  = type.GetMethod("UpdateCachedWindows", priv);
            var nextEnumField = type.GetField("_nextEnumTime", priv);
            updateCached?.Invoke(handler, null);
            nextEnumField?.SetValue(handler, Time.unscaledTime + 1f);

            snappedField.SetValue(handler, targetHwnd);
            guardZoneField?.SetValue(handler, false);

            // Reset wasSitting so Update() fires the WindowSitIndex blend tree assignment
            type.GetField("wasSitting",     priv)?.SetValue(handler, false);
            type.GetField("seatCalibrated", priv)?.SetValue(handler, false);

            animator.SetBool("isWindowSit",  true);
            animator.SetBool("isTaskbarSit", false);
            animator.Update(0f);

            settleFrames?.SetValue(handler, 1);
            settleRecalib?.SetValue(handler, true);
            fracField?.SetValue(handler, 0.5f);
            lastSnapYField?.SetValue(handler, tr.Top);
            recentUnsnapField?.SetValue(handler, false);

            int snapGuard = (int?)type.GetField("snapGuardFrames", pub)?.GetValue(handler) ?? 8;
            int snapLatch = (int?)type.GetField("snapLatchFrames", pub)?.GetValue(handler) ?? 18;
            guardField?.SetValue(handler, Mathf.Max(1, snapGuard));
            latchField?.SetValue(handler, Mathf.Max(1, snapLatch));

            bool enableSmooth = (bool?)type.GetField("enableSnapSmoothing", pub)?.GetValue(handler) ?? false;
            smoothField?.SetValue(handler, enableSmooth);
            velXField?.SetValue(handler, 0f);
            velYField?.SetValue(handler, 0f);
            prevRectField?.SetValue(handler, false);

            setTopMost?.Invoke(handler, new object[] { true });
            rebuildOccluders?.Invoke(handler, null);
            updateOccluders?.Invoke(handler, null);

            var rectType = type.GetNestedType("RECT", BindingFlags.NonPublic | BindingFlags.Public);
            if (rectType != null && pinToTarget != null)
            {
                var rect = Activator.CreateInstance(rectType);
                rectType.GetField("Left")?.SetValue(rect, tr.Left);
                rectType.GetField("Top")?.SetValue(rect, tr.Top);
                rectType.GetField("Right")?.SetValue(rect, tr.Right);
                rectType.GetField("Bottom")?.SetValue(rect, tr.Bottom);
                pinToTarget.Invoke(handler, new object[] { rect });
            }

            StartCoroutine(PulseDraggingForSnap(handler, targetHwnd));
            return "ok";
        }

        private IEnumerator PulseDraggingForSnap(AvatarWindowHandler handler, IntPtr targetHwnd)
        {
            var ctrl = FindAnyObjectByType<AvatarAnimatorController>();
            if (ctrl == null) yield break;

            var type = typeof(AvatarWindowHandler);
            var priv = BindingFlags.NonPublic | BindingFlags.Instance;
            var pub = BindingFlags.Public | BindingFlags.Instance;

            var calibrate = type.GetMethod("CalibrateSeatAnchorToDesktopY", priv);
            var computeSeat = type.GetMethod("ComputeSeatDesktop", priv);
            var pinToTarget = type.GetMethod("PinToTarget", priv);
            var setTopMost = type.GetMethod("SetTopMost", priv);
            var fracField = type.GetField("snapFraction", priv);
            var settleFrames = type.GetField("_postSettleFrames", priv);
            var settleRecalib = type.GetField("_postSettleRecalib", priv);

            // Phase 1: start drag, wait for animator to blend into drag pose
            ctrl.isDragging = true;
            ctrl.animator.SetBool("isDragging", true);

            for (int i = 0; i < 15; i++)
                yield return null;

            // Phase 2: bones are now in drag pose — calibrate seat anchor
            if (GetWindowRect(targetHwnd, out WinRect tr))
            {
                float seatOffsetPx = (float?)type.GetField("seatOffsetPx", pub)?.GetValue(handler) ?? 0f;
                calibrate?.Invoke(handler, new object[] { (float)(tr.Top + seatOffsetPx) });

                // Recalculate snap fraction with 30px edge margin
                if (computeSeat != null)
                {
                    var args = new object[] { 0f, 0f };
                    bool ok = (bool)computeSeat.Invoke(handler, args);
                    if (ok)
                    {
                        float px2 = (float)args[0];
                        float winW = Mathf.Max(1, tr.Right - tr.Left);
                        float margin = 30f / winW;
                        fracField?.SetValue(handler, Mathf.Clamp(
                            (px2 - tr.Left) / winW,
                            margin,
                            1f - margin));
                    }
                    else
                    {
                        // Fallback: centre with margin
                        fracField?.SetValue(handler, 0.5f);
                    }
                }

                settleFrames?.SetValue(handler, 0);
                settleRecalib?.SetValue(handler, false);

                // Pin with calibrated position
                var rectType = type.GetNestedType("RECT", BindingFlags.NonPublic | BindingFlags.Public);
                if (rectType != null && pinToTarget != null)
                {
                    var rect = Activator.CreateInstance(rectType);
                    rectType.GetField("Left")?.SetValue(rect, tr.Left);
                    rectType.GetField("Top")?.SetValue(rect, tr.Top);
                    rectType.GetField("Right")?.SetValue(rect, tr.Right);
                    rectType.GetField("Bottom")?.SetValue(rect, tr.Bottom);
                    pinToTarget.Invoke(handler, new object[] { rect });
                }
            }

            // Phase 3: hold drag so animator completes sit transition
            for (int i = 0; i < 20; i++)
                yield return null;

            ctrl.isDragging = false;
            ctrl.animator.SetBool("isDragging", false);

            // Set topmost WITHOUT stealing focus after everything settles
            setTopMost?.Invoke(handler, new object[] { true });
        }

        // ── Head pat ──────────────────────────────────────────────────────────

        public string Headpat()
        {
            var ctrl = FindAnyObjectByType<AvatarAnimatorController>();
            if (ctrl == null) return "ERROR: AvatarAnimatorController not found";

            var animator = ctrl.animator;
            animator.CrossFadeInFixedTime("Head Pat", 0.1f, animator.GetLayerIndex("Base Layer"));
            animator.CrossFadeInFixedTime("Head Pat", 0.1f, animator.GetLayerIndex("Face layer"));
            return "ok";
        }

        // ── Blendshapes ───────────────────────────────────────────────────────

        /// <summary>Returns all blendshape names grouped by mesh, for debugging / profile authoring.</summary>
        public List<MeshBlendshapeInfo> GetBlendshapes()
        {
            var result = new List<MeshBlendshapeInfo>();
            foreach (var smr in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
            {
                if (smr.sharedMesh == null) continue;
                var info = new MeshBlendshapeInfo { mesh = smr.gameObject.name, shapes = new List<string>() };
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                    info.shapes.Add(smr.sharedMesh.GetBlendShapeName(i));
                result.Add(info);
            }
            return result;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private IEnumerator HoldMood(string mood)
        {
            var wait = new WaitForEndOfFrame();
            Dictionary<string, SkinnedMeshRenderer> smrCache = null;
            GameObject lastRoot = null;
            PuppetMasterMoodProfile.AvatarProfile activeProfile = null;

            while (true)
            {
                yield return wait;
                if (_moodProfile == null) continue;

                var currentRoot = GetAvatarRoot();
                if (currentRoot != lastRoot || activeProfile == null)
                {
                    smrCache = null;
                    lastRoot = currentRoot;
                    string avatarName = GetAvatarDisplayName();
                    activeProfile = _moodProfile.GetProfileFor(avatarName);
                    if (activeProfile != null)
                        Debug.Log("[PuppetMasterActions] HoldMood — matched profile: " + activeProfile.avatarName);
                }

                if (activeProfile == null || currentRoot == null) continue;

                if (smrCache == null)
                {
                    smrCache = new Dictionary<string, SkinnedMeshRenderer>();
                    foreach (var smr in currentRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (smr != null && smr.sharedMesh != null && !smrCache.ContainsKey(smr.gameObject.name))
                            smrCache[smr.gameObject.name] = smr;
                }

                PuppetMasterMoodProfile.MoodGroup group = null;
                foreach (var g in activeProfile.moods)
                    if (string.Equals(g.name, mood, StringComparison.OrdinalIgnoreCase))
                    { group = g; break; }

                if (group == null) continue;

                foreach (var g in activeProfile.moods)
                    foreach (var t in g.targets)
                        if (smrCache.TryGetValue(t.meshName, out var smr))
                            SetBlendShape(smr, t.blendShapeName, 0f);

                foreach (var t in group.targets)
                    if (smrCache.TryGetValue(t.meshName, out var smr))
                        SetBlendShape(smr, t.blendShapeName, t.weight);
            }
        }

        private IEnumerator ShowMessageCoroutine(string text)
        {
            var mc = FindAnyObjectByType<AvatarMinecraftMessages>();
            if (mc == null) { Debug.LogWarning("[PuppetMasterActions] AvatarMinecraftMessages not found."); yield break; }

            mc.enableMinecraftMessages = true;

            var showEvent = typeof(AvatarMinecraftMessages).GetMethod(
                "ShowEvent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (showEvent != null)
            {
                var mcMessagesField = typeof(AvatarMinecraftMessages).GetField(
                    "mcMessages",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                var original = mcMessagesField.GetValue(mc);
                var tempList = new List<AvatarMinecraftMessages.McMessageEntry>
            {
                new AvatarMinecraftMessages.McMessageEntry
                {
                    text = text,
                    type = AvatarMinecraftMessages.McEventType.Entity
                }
            };
                mcMessagesField.SetValue(mc, tempList);
                showEvent.Invoke(mc, new object[] { AvatarMinecraftMessages.McEventType.Entity, "", "" });
                mcMessagesField.SetValue(mc, original);
            }
            else
            {
                mc.ShowEntityMessage(text);
            }
        }

        private GameObject GetAvatarRoot()
        {
            var loader = FindAnyObjectByType<VRMLoader>();
            if (loader != null)
            {
                var model = loader.GetCurrentModel();
                if (model != null) return model;
                if (loader.mainModel != null) return loader.mainModel;
            }
            return null;
        }

        private string GetAvatarDisplayName()
        {
            var loader = FindAnyObjectByType<VRMLoader>();
            var model = loader?.GetCurrentModel();
            if (model == null) return "Default";

            var vrm10 = model.GetComponent<UniVRM10.Vrm10Instance>();
            if (vrm10?.Vrm?.Meta != null && !string.IsNullOrEmpty(vrm10.Vrm.Meta.Name))
                return vrm10.Vrm.Meta.Name;

            var vrmMeta = model.GetComponent<VRM.VRMMeta>();
            if (vrmMeta?.Meta != null && !string.IsNullOrEmpty(vrmMeta.Meta.Title))
                return vrmMeta.Meta.Title;

            var path = SaveLoadHandler.Instance?.data?.selectedModelPath;
            if (!string.IsNullOrEmpty(path))
                return System.IO.Path.GetFileNameWithoutExtension(path);

            return model.name;
        }

        private void SetBlendShape(SkinnedMeshRenderer smr, string name, float weight)
        {
            if (smr == null) return;
            for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                if (string.Equals(smr.sharedMesh.GetBlendShapeName(i), name, StringComparison.OrdinalIgnoreCase))
                { smr.SetBlendShapeWeight(i, weight); return; }
        }

        // ── Data transfer objects ─────────────────────────────────────────────

        public class AvatarStatus
        {
            public string mood;
            public bool dancing;
            public bool walking;
            public bool bigscreen;
            public string avatar;
            public float size;
            public bool isWindowSit;
            public string snappedWindowTitle;
        }

        public class AnimatorParamInfo
        {
            public string name;
            public string type;
        }

        public class MeshBlendshapeInfo
        {
            public string mesh;
            public List<string> shapes;
        }
    }
}