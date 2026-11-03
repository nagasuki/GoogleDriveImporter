#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace GoogleDriveImporter
{
    internal static class EditorCoroutineRunner
    {
        private sealed class State
        {
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
            public object CurrentYield;
            public Action Completed;
            public Action<Exception> Failed;
        }

        private static readonly List<State> Running = new List<State>();
        private static readonly Action<Func<bool>, Action<Exception>> StartUniTask = CreateUniTaskStarter();
        private static bool isInitialized;

        public static void Start(IEnumerator routine, Action completed = null, Action<Exception> failed = null)
        {
            if (routine == null)
                throw new ArgumentNullException(nameof(routine));

            var state = new State
            {
                Completed = completed,
                Failed = failed
            };
            state.Stack.Push(routine);
            if (StartUniTask != null)
            {
                try
                {
                    StartUniTask(() => AdvanceWithUniTask(state), failed ?? Debug.LogException);
                }
                catch (TargetInvocationException exception)
                {
                    DisposeStack(state.Stack);
                    Exception cause = exception.InnerException ?? exception;
                    if (failed == null)
                        Debug.LogException(cause);
                    else
                        failed(cause);
                }
                return;
            }

            Initialize();
            Running.Add(state);
        }

        private static Action<Func<bool>, Action<Exception>> CreateUniTaskStarter()
        {
            // Discover once per domain reload. This works for both UPM and Assets
            // installations without assembly references or project-wide defines.
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type taskType = assembly.GetType("Cysharp.Threading.Tasks.UniTask");
                if (taskType == null)
                    continue;

                Type timingType = assembly.GetType("Cysharp.Threading.Tasks.PlayerLoopTiming");
                Type taskExtensions = assembly.GetType("Cysharp.Threading.Tasks.UniTaskExtensions");
                if (timingType == null || taskExtensions == null)
                    break;

                MethodInfo waitUntil = taskType.GetMethod("WaitUntil",
                    new[] { typeof(Func<bool>), timingType, typeof(CancellationToken), typeof(bool) });
                MethodInfo forget = taskExtensions.GetMethod("Forget",
                    new[] { taskType, typeof(Action<Exception>), typeof(bool) });
                if (waitUntil == null || forget == null)
                    break;

                object timing = Enum.Parse(timingType, "Update");
                return (isCompleted, failed) =>
                {
                    object task = waitUntil.Invoke(null, new[] { isCompleted, timing, CancellationToken.None, (object)false });
                    forget.Invoke(null, new[] { task, failed, (object)false });
                };
            }
            return null;
        }

        private static bool AdvanceWithUniTask(State state)
        {
            try
            {
                if (Advance(state))
                    return false;
                state.Completed?.Invoke();
                return true;
            }
            catch
            {
                DisposeStack(state.Stack);
                throw;
            }
        }

        public static IEnumerator RunInParallel(IList<IEnumerator> routines, int maxConcurrent)
        {
            if (routines == null)
                throw new ArgumentNullException(nameof(routines));
            if (maxConcurrent <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrent));

            return new ParallelRoutine(routines, maxConcurrent);
        }

        private static void Initialize()
        {
            if (isInitialized)
                return;

            EditorApplication.update += Update;
            isInitialized = true;
        }

        private static void Update()
        {
            for (int index = Running.Count - 1; index >= 0; index--)
            {
                State state = Running[index];
                bool completed = false;
                Exception failure = null;
                try
                {
                    completed = !Advance(state);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                if (!completed && failure == null)
                    continue;

                Running.RemoveAt(index);
                DisposeStack(state.Stack);
                if (failure != null)
                {
                    if (state.Failed == null)
                        Debug.LogException(failure);
                    else
                        state.Failed(failure);
                }
                else
                    state.Completed?.Invoke();
            }

            if (Running.Count == 0 && isInitialized)
            {
                EditorApplication.update -= Update;
                isInitialized = false;
            }
        }

        private static bool Advance(State state)
        {
            while (true)
            {
                if (state.CurrentYield != null)
                {
                    if (!IsReady(state.CurrentYield))
                        return true;
                    state.CurrentYield = null;
                }

                if (state.Stack.Count == 0)
                    return false;

                IEnumerator current = state.Stack.Peek();
                if (!current.MoveNext())
                {
                    (state.Stack.Pop() as IDisposable)?.Dispose();
                    continue;
                }

                object yielded = current.Current;
                if (yielded is IEnumerator nested)
                {
                    state.Stack.Push(nested);
                    continue;
                }

                if (yielded == null)
                    return true;

                if (!IsReady(yielded))
                    state.CurrentYield = yielded;

                return true;
            }
        }

        private static bool IsReady(object yielded)
        {
            if (yielded is AsyncOperation operation)
                return operation.isDone;
            if (yielded is CustomYieldInstruction customYieldInstruction)
                return !customYieldInstruction.keepWaiting;
            if (yielded is YieldInstruction)
                return false;
            return true;
        }

        private static void DisposeStack(Stack<IEnumerator> stack)
        {
            while (stack.Count > 0)
                (stack.Pop() as IDisposable)?.Dispose();
        }

        private sealed class ParallelRoutine : IEnumerator, IDisposable
        {
            private readonly IList<IEnumerator> routines;
            private readonly List<State> children = new List<State>();
            private readonly int maxConcurrent;
            private int nextRoutineIndex;
            private Exception failure;

            public ParallelRoutine(IList<IEnumerator> routines, int maxConcurrent)
            {
                this.routines = routines;
                this.maxConcurrent = maxConcurrent;
                for (int index = 0; index < routines.Count; index++)
                {
                    if (routines[index] == null)
                        throw new ArgumentException("Parallel routines cannot contain null entries.", nameof(routines));
                }

                StartNextRoutines();
            }

            public object Current
            {
                get { return null; }
            }

            public bool MoveNext()
            {
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    State child = children[index];
                    try
                    {
                        if (Advance(child))
                            continue;

                        children.RemoveAt(index);
                    }
                    catch (Exception exception)
                    {
                        DisposeStack(child.Stack);
                        children.RemoveAt(index);
                        if (failure == null)
                            failure = exception;
                    }
                }

                if (failure == null)
                    StartNextRoutines();

                if (children.Count > 0)
                    return true;

                if (failure != null)
                    throw failure;

                return false;
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
                foreach (State child in children)
                    DisposeStack(child.Stack);
                children.Clear();
            }

            private void StartNextRoutines()
            {
                while (failure == null
                    && nextRoutineIndex < routines.Count
                    && children.Count < maxConcurrent)
                {
                    var child = new State();
                    child.Stack.Push(routines[nextRoutineIndex]);
                    children.Add(child);
                    nextRoutineIndex++;
                }
            }

        }
    }
}
#endif
