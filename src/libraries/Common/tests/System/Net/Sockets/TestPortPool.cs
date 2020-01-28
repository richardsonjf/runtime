// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Test.Common;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.DotNet.RemoteExecutor;

namespace System.Net.Sockets.Tests
{
    internal readonly struct PortAssignment : IDisposable
    {
        public int Port { get; }

        internal PortAssignment(int port) => Port = port;

        public void Dispose() => TestPortPool.Return(this);
    }

    internal class TestPortPoolExhaustedException : Exception
    {
        public TestPortPoolExhaustedException()
            : base($"TestPortPool failed to find an available port after {TestPortPool.ThrowExhaustedAfter} attempts")
        {
        }
    }

    /// <summary>
    /// Distributes ports from the range defined by <see cref="Configuration.Sockets.TestPoolPortRange"/>
    /// in a synchronized way. Synchronization does not work across multiple processes.
    /// </summary>
    internal static class TestPortPool
    {
        internal const int ThrowExhaustedAfter = 10;

        private static readonly int MinPort =
            System.Net.Test.Common.Configuration.Sockets.TestPoolPortRange.Min;
        private static readonly int MaxPort =
            System.Net.Test.Common.Configuration.Sockets.TestPoolPortRange.Max;
        private static readonly int PortRangeLength = MaxPort - MinPort;

        private static readonly ConcurrentDictionary<int, int> s_usedPorts = new ConcurrentDictionary<int, int>();
        private static int s_counter = int.MinValue;

        public static PortAssignment RentPort()
        {
            int cnt = 0;
            for (;cnt < ThrowExhaustedAfter; cnt++)
            {
                // Although race conditions may happen theoretically because the following code block is not atomic,
                // it requires the s_counter to move at least PortRangeLength steps between Increment and TryAdd,
                // which is very unlikely considering the actual port range.

                long portLong = (long)Interlocked.Increment(ref s_counter) - int.MinValue;
                portLong = (portLong % PortRangeLength) + MinPort;
                int port = (int)portLong;

                if (s_usedPorts.TryAdd(port, 0))
                {
                    return new PortAssignment(port);
                }
            }

            throw new TestPortPoolExhaustedException();
        }

        public static void Return(PortAssignment portAssignment)
        {
            s_usedPorts.TryRemove(portAssignment.Port, out _);
        }

        public static PortAssignment RentPortAndBindSocket(Socket socket, IPAddress address)
        {
            PortAssignment assignment = RentPort();
            socket.Bind(new IPEndPoint(address, assignment.Port));
            return assignment;
        }
    }
}
