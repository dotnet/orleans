/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;

namespace Orleans.Runtime.Scheduler
{
    internal class SchedulingContext : ISchedulingContext
    {
        public ActivationData Activation { get; private set; }

        public SystemTarget SystemTarget { get; private set; }

        public int DispatcherTarget { get; private set; }

        public SchedulingContextType ContextType { get; private set; }

        private readonly bool isLowPrioritySystemTarget;

        public SchedulingContext(ActivationData activation)
        {
            Activation = activation;
            ContextType = SchedulingContextType.Activation;
            isLowPrioritySystemTarget = false;
        }

        internal SchedulingContext(SystemTarget systemTarget, bool lowPrioritySystemTarget)
        {
            SystemTarget = systemTarget;
            ContextType = SchedulingContextType.SystemTarget;
            isLowPrioritySystemTarget = lowPrioritySystemTarget;
        }

        internal SchedulingContext(int dispatcherTarget)
        {
            DispatcherTarget = dispatcherTarget;
            ContextType = SchedulingContextType.SystemThread;
            isLowPrioritySystemTarget = false;
        }

        public bool IsSystemPriorityContext
        {
            get
            {
                switch (ContextType)
                {
                    case SchedulingContextType.Activation:
                        return false;

                    case SchedulingContextType.SystemTarget:
                        return !isLowPrioritySystemTarget;

                    case SchedulingContextType.SystemThread:
                        return true;

                    default:
                        return false;
                }
            }
        }

        #region IEquatable<ISchedulingContext> Members

        public bool Equals(ISchedulingContext other)
        {
            return AreSame(other);
        }

        #endregion

        public override bool Equals(object obj)
        {
            return AreSame(obj);
        }

        private bool AreSame(object obj)
        {
            var other = obj as SchedulingContext;
            switch (ContextType)
            {
                case SchedulingContextType.Activation:
                    return other != null && Activation.Equals(other.Activation);

                case SchedulingContextType.SystemTarget:
                    return other != null && SystemTarget.Equals(other.SystemTarget);

                case SchedulingContextType.SystemThread:
                    return other != null && DispatcherTarget.Equals(other.DispatcherTarget);

                default:
                    return false;
            }
        }

        public override int GetHashCode()
        {
            switch (ContextType)
            {
                case SchedulingContextType.Activation:
                    return Activation.ActivationId.Key.GetHashCode();

                case SchedulingContextType.SystemTarget:
                    return SystemTarget.ActivationId.Key.GetHashCode();

                case SchedulingContextType.SystemThread:
                    return DispatcherTarget;

                default:
                    return 0;
            }
        }

        public override string ToString()
        {
            switch (ContextType)
            {
                case SchedulingContextType.Activation:
                    return Activation.ToString();

                case SchedulingContextType.SystemTarget:
                    return SystemTarget.ToString();

                case SchedulingContextType.SystemThread:
                    return String.Format("DispatcherTarget{0}", DispatcherTarget);

                default:
                    return "";
            }
        }

        public string Name 
        {
            get
            {
                switch (ContextType)
                {
                    case SchedulingContextType.Activation:
                        return Activation.Name;

                    case SchedulingContextType.SystemTarget:
                        return SystemTarget.GrainId.ToString();

                    case SchedulingContextType.SystemThread:
                        return String.Format("DispatcherTarget{0}", DispatcherTarget);

                    default:
                        return "";
                }
            }
        }
    }
}
