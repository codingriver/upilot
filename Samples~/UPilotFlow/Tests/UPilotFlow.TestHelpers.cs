using System;
using System.Collections;
using System.Threading.Tasks;

namespace CodingRiver.UPilot.Flow
{
    public static class UPilotFlowTestTaskUtility
    {
        public static IEnumerator Await(Task task)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.Exception != null)
            {
                throw task.Exception.Flatten().InnerException;
            }
        }

        public static IEnumerator Await<T>(Task<T> task, Action<T> onCompleted)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.Exception != null)
            {
                throw task.Exception.Flatten().InnerException;
            }

            onCompleted?.Invoke(task.Result);
        }

        public static IEnumerator AwaitFailure(Task task, Action<Exception> onFailed)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.Exception == null)
            {
                throw new Exception("Expected task to fail, but it completed successfully.");
            }

            onFailed?.Invoke(task.Exception.Flatten().InnerException);
        }
    }
}
