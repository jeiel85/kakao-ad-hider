using System;
using System.Collections.Generic;
using System.Threading;
using static KakaoAdHider.Native;

namespace KakaoAdHider
{
    /// <summary>
    /// SetWindowRgn은 대상 창에 동기 메시지를 보낸다. 트레이 UI를 막지 않도록
    /// 표시 영역 상태를 단일 작업 스레드에서만 다루며, 창별 대기 요청은 하나로 합친다.
    /// </summary>
    internal sealed class BetaCropWorker
    {
        readonly BetaWindowCrop crop = new();
        readonly object gate = new();
        AutoResetEvent wake;
        readonly Dictionary<IntPtr, uint> pending = new();
        bool stopping, prunePending;
        Action stopped;

        public void Apply(IntPtr main)
        {
            lock (gate)
            {
                if (stopping) return;
                if (wake == null)
                {
                    // 일반 카카오톡만 사용하면 작업 스레드와 이벤트 핸들을 만들지 않는다.
                    wake = new AutoResetEvent(false);
                    new Thread(Run) { IsBackground = true, Name = "Kakao beta window region" }.Start();
                }
                var alreadyPending = pending.ContainsKey(main);
                pending[main] = ProcessIdOf(main);
                if (!alreadyPending) wake.Set();
            }
        }

        public void Prune()
        {
            lock (gate)
            {
                if (stopping || wake == null || prunePending) return;
                prunePending = true;
                wake.Set();
            }
        }

        public void Stop(Action onStopped)
        {
            var completeImmediately = false;
            lock (gate)
            {
                if (stopping) return;
                stopping = true;
                if (wake == null) completeImmediately = true;
                else
                {
                    stopped = onStopped;
                    pending.Clear();
                    prunePending = false;
                    wake.Set();
                }
            }
            // 콜백이 작업자를 다시 호출할 수 있으므로 잠금 밖에서 실행한다.
            if (completeImmediately) onStopped?.Invoke();
        }

        void Run()
        {
            try
            {
                while (true)
                {
                    wake.WaitOne();
                    KeyValuePair<IntPtr, uint>[] batch;
                    bool prune;
                    lock (gate)
                    {
                        if (stopping) break;
                        batch = new KeyValuePair<IntPtr, uint>[pending.Count];
                        ((ICollection<KeyValuePair<IntPtr, uint>>)pending).CopyTo(batch, 0);
                        pending.Clear();
                        prune = prunePending;
                        prunePending = false;
                    }
                    var applied = prune && batch.Length > 0 ? new HashSet<IntPtr>() : null;
                    foreach (var item in batch)
                    {
                        lock (gate) { if (stopping) break; }
                        // 요청 당시의 좌표 대신 실제 실행 시점의 광고 구조를 다시 확인한다.
                        if (IsWindow(item.Key) && ProcessIdOf(item.Key) == item.Value && BetaLayout.IsQtWindow(item.Key))
                        {
                            crop.Apply(item.Key, BetaLayout.FindPopups(item.Key));
                            applied?.Add(item.Key);
                        }
                    }
                    lock (gate) { if (stopping) continue; }
                    if (prune) crop.Prune(applied);
                }
                crop.RestoreAll();
            }
            finally
            {
                wake.Dispose();
                stopped?.Invoke();
            }
        }
    }
}
