#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace GoogleDriveImporter
{
    internal static class EditorAsyncRunner
    {
        private sealed class UniTaskBridge
        {
            public Func<Func<bool>, Task> WaitUntil;
            public Func<AsyncOperation, Task> WaitForOperation;
        }

        private static readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        private static readonly UniTaskBridge UniTask = CreateUniTaskBridge();

        static EditorAsyncRunner()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Lifetime.Cancel;
            EditorApplication.quitting += Lifetime.Cancel;
        }

        // The EditorWindow is the only async-void boundary. Every operation below
        // it returns a Task, and every failure is observed here or by its caller.
        public static async void Start(Func<Task> operation, Action completed = null, Action<Exception> failed = null)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            try
            {
                await operation();
                completed?.Invoke();
            }
            catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
            {
                // Unwind requests and finally blocks before the editor reloads.
            }
            catch (Exception exception)
            {
                if (failed == null)
                    Debug.LogException(exception);
                else
                    failed(exception);
            }
        }

        public static async Task WaitUntil(Func<bool> predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));
            Lifetime.Token.ThrowIfCancellationRequested();

            if (UniTask != null)
            {
                await UniTask.WaitUntil(predicate);
                return;
            }

            var completion = new TaskCompletionSource<bool>();
            EditorApplication.CallbackFunction update = () =>
            {
                try
                {
                    if (predicate())
                        completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            };
            EditorApplication.update += update;
            using (Lifetime.Token.Register(() => completion.TrySetCanceled()))
            {
                try { await completion.Task; }
                finally { EditorApplication.update -= update; }
            }
        }

        public static async Task WaitForOperation(AsyncOperation operation, Action onProgress = null)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));
            Lifetime.Token.ThrowIfCancellationRequested();
            if (operation.isDone)
                return;

            // Progress updates stay on the editor thread for both backends.
            // The Task backend resumes on completion, without polling isDone.
            EditorApplication.CallbackFunction progress = onProgress == null
                ? (EditorApplication.CallbackFunction)null
                : () => onProgress();
            if (progress != null)
                EditorApplication.update += progress;
            try
            {
                if (UniTask != null)
                    await UniTask.WaitForOperation(operation);
                else
                    await WaitForOperationTask(operation);
            }
            finally
            {
                if (progress != null)
                    EditorApplication.update -= progress;
            }
        }

        private static async Task WaitForOperationTask(AsyncOperation operation)
        {
            var completion = new TaskCompletionSource<bool>();
            Action<AsyncOperation> completed = _ => completion.TrySetResult(true);
            operation.completed += completed;
            using (Lifetime.Token.Register(() => completion.TrySetCanceled()))
            {
                try
                {
                    if (operation.isDone)
                        completion.TrySetResult(true);
                    await completion.Task;
                }
                finally { operation.completed -= completed; }
            }
        }

        public static async Task RunInParallel<T>(IList<T> items, int maxConcurrent, Func<T, Task> execute)
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));
            if (maxConcurrent <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
            if (execute == null)
                throw new ArgumentNullException(nameof(execute));

            int next = 0;
            bool hasFailure = false;
            // Workers are launched and resumed on Unity's synchronization context.
            // Only file hashing belongs on a background thread, never Unity APIs.
            async Task Worker()
            {
                while (!hasFailure && next < items.Count)
                {
                    T item = items[next++];
                    try { await execute(item); }
                    catch { hasFailure = true; throw; }
                }
            }

            var workers = new Task[Math.Min(maxConcurrent, items.Count)];
            for (int index = 0; index < workers.Length; index++)
                workers[index] = Worker();
            // Observe all workers, including in-flight requests after a failure.
            await Task.WhenAll(workers);
        }

        private static UniTaskBridge CreateUniTaskBridge()
        {
            // Resolve once per domain reload for both UPM and Assets installs.
            // No hard dependency, assembly references or project defines needed.
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type taskType = assembly.GetType("Cysharp.Threading.Tasks.UniTask");
                if (taskType == null)
                    continue;
                Type timingType = assembly.GetType("Cysharp.Threading.Tasks.PlayerLoopTiming");
                Type taskExtensions = assembly.GetType("Cysharp.Threading.Tasks.UniTaskExtensions");
                Type unityExtensions = assembly.GetType("Cysharp.Threading.Tasks.UnityAsyncExtensions");
                if (timingType == null || taskExtensions == null || unityExtensions == null)
                    break;

                MethodInfo waitUntil = taskType.GetMethod("WaitUntil",
                    new[] { typeof(Func<bool>), timingType, typeof(CancellationToken), typeof(bool) });
                MethodInfo toUniTask = unityExtensions.GetMethod("ToUniTask",
                    new[] { typeof(AsyncOperation), typeof(IProgress<float>), timingType, typeof(CancellationToken), typeof(bool) });
                MethodInfo asTask = taskExtensions.GetMethod("AsTask", new[] { taskType });
                if (waitUntil == null || toUniTask == null || asTask == null)
                    break;

                object timing = Enum.Parse(timingType, "Update");
                Task Convert(MethodInfo method, object[] arguments)
                {
                    try
                    {
                        object task = method.Invoke(null, arguments);
                        return (Task)asTask.Invoke(null, new[] { task });
                    }
                    catch (TargetInvocationException exception)
                    {
                        ExceptionDispatchInfo.Capture(exception.InnerException ?? exception).Throw();
                        throw;
                    }
                }
                return new UniTaskBridge
                {
                    WaitUntil = predicate => Convert(waitUntil,
                        new object[] { predicate, timing, Lifetime.Token, true }),
                    WaitForOperation = operation => Convert(toUniTask,
                        new object[] { operation, null, timing, Lifetime.Token, true })
                };
            }
            return null;
        }
    }
}
#endif
