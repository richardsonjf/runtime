﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Net.Sockets.Tests;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace System.Net.Test.Common
{


    // Tests are relatively long-running, and we do not expect the TestPortPool to be changed frequently
    [OuterLoop]
    [Collection(nameof(DisableParallelExecution))]
    public class TestPortPoolTests
    {
        [CollectionDefinition(nameof(DisableParallelExecution), DisableParallelization = true)]
        public class DisableParallelExecution {}

        // Port range 25010-25470 is likely unused:
        // https://www.iana.org/assignments/service-names-port-numbers/service-names-port-numbers.txt
        private const int FirstUnusedPort = 25010;

        private static RemoteInvokeOptions CreateRemoteOptions(string portRangeString)
        {
            return new RemoteInvokeOptions()
            {
                StartInfo = new ProcessStartInfo()
                {
                    Environment = {["COREFX_NET_SOCKETS_PORTPOOLRANGE"] = portRangeString}
                }
            };
        }

        [Fact]
        public static void PortRange_IsConfigurable()
        {
            RemoteInvokeOptions options = CreateRemoteOptions(" 10    142  ");

            static void RunTest()
            {
                var range = Configuration.Sockets.TestPoolPortRange;
                Assert.Equal(10, range.Min);
                Assert.Equal(142, range.Max);
            }

            RemoteExecutor.Invoke(RunTest, options).Dispose();
        }

        [Fact]
        public static void PortRange_HasCorrectDefaults()
        {
            static void RunTest()
            {
                var range = Configuration.Sockets.TestPoolPortRange;

                Assert.True(range.Min < range.Max);
                Assert.True(range.Max < 32768);
                Assert.True(range.Min > 15000);
            }

            RemoteExecutor.Invoke(RunTest).Dispose();
        }


        [Theory]
        [InlineData(FirstUnusedPort, FirstUnusedPort + 300)]
        [InlineData(FirstUnusedPort, FirstUnusedPort + 3)]
        public static void AllPortsAreWithinRange(int minOuter, int maxOuter)
        {
            static void RunTest(string minStr, string maxStr)
            {
                int min = int.Parse(minStr);
                int max = int.Parse(maxStr);
                int rangeLength = max - min;

                // Modify the counter to test the behavior on overflow:
                typeof(TestPortPool).GetField("s_counter", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, int.MaxValue - 5);

                HashSet<int> allVisitedValues = new HashSet<int>();
                for (long i = 0; i < rangeLength * 2 + 42; i++)
                {
                    using PortLease lease = TestPortPool.RentPort();
                    allVisitedValues.Add(lease.Port);
                }

                Assert.Equal(rangeLength, allVisitedValues.Count);
                Assert.Equal(min, allVisitedValues.Min());

                // Maximum is exclusive:
                Assert.Equal(max - 1, allVisitedValues.Max());
            }

            RemoteInvokeOptions options = CreateRemoteOptions($"{minOuter} {maxOuter}");
            RemoteExecutor.Invoke(RunTest, minOuter.ToString(), maxOuter.ToString(), options).Dispose();
        }

        [Fact]
        public void WhenExhausted_Throws()
        {
            static void RunTest()
            {
                for (int i = 0; i < 20; i++)
                {
                    TestPortPool.RentPort();
                }

                Assert.Throws<TestPortPoolExhaustedException>(() => TestPortPool.RentPort());
            }

            RemoteInvokeOptions options = CreateRemoteOptions($"100 120");
            RemoteExecutor.Invoke(RunTest, options).Dispose();
        }

        [Theory]
        [InlineData(1000)]
        [InlineData(200)]
        public void ConcurrentAccess_AssignedPortsAreUnique(int portRangeLength)
        {
            const int levelOfParallelism = 8;
            const int requestPerThread = 200;
            const int maxDelayInTicks = 500;
            const int returnPortsAfterTicks = 10000;

            static async Task<int> RunTest()
            {
                Task[] workItems = new Task[levelOfParallelism];

                ConcurrentDictionary<int, int> currentPorts = new ConcurrentDictionary<int, int>();

                for (int i = 0; i < levelOfParallelism; i++)
                {
                    workItems[i] = Task.Factory.StartNew( ii =>
                    {
                        Random rnd = new Random((int)ii);

                        List<PortLease> livingAssignments = new List<PortLease>();

                        Stopwatch sw = Stopwatch.StartNew();
                        long returnPortsAfter = rnd.Next(returnPortsAfterTicks);

                        for (int j = 0; j < requestPerThread; j++)
                        {
                            Thread.Sleep(TimeSpan.FromTicks(rnd.Next(maxDelayInTicks)));

                            PortLease lease = TestPortPool.RentPort();

                            Assert.True(currentPorts.TryAdd(lease.Port, 0),
                                "Same port has been rented more than once!");

                            livingAssignments.Add(lease);

                            if (sw.ElapsedTicks > returnPortsAfter) Reset();
                        }

                        void Reset()
                        {
                            sw.Stop();

                            foreach (PortLease assignment in livingAssignments)
                            {
                                Assert.True(currentPorts.TryRemove(assignment.Port, out _));
                                assignment.Dispose();
                            }
                            livingAssignments.Clear();
                            returnPortsAfter = rnd.Next(returnPortsAfterTicks);
                            sw.Start();
                        }
                    }, i);

                }

                await Task.WhenAll(workItems);

                return RemoteExecutor.SuccessExitCode;
            }

            RemoteInvokeOptions options = CreateRemoteOptions($"{FirstUnusedPort} {FirstUnusedPort + portRangeLength}");
            RemoteExecutor.Invoke(RunTest, options).Dispose();
        }

        [Fact]
        public void TestSocketIntegration()
        {
            static async Task<int> RunTest()
            {
                const int levelOfParallelism = 8;
                const int requestPerThread = 200;

                Task[] workItems = Enumerable.Repeat(Task.Run(() =>
                {
                    for (int i = 0; i < requestPerThread; i++)
                    {
                        using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        using PortLease lease = TestPortPool.RentPortAndBindSocket(socket, IPAddress.Loopback);

                        Assert.True(socket.IsBound);
                        IPEndPoint ep = (IPEndPoint)socket.LocalEndPoint;
                        Assert.Equal(lease.Port, ep.Port);
                    }
                }), levelOfParallelism).ToArray();

                await Task.WhenAll(workItems);
                return RemoteExecutor.SuccessExitCode;
            }

            RemoteExecutor.Invoke(RunTest).Dispose();
        }

        private static readonly int[] s_mockSystemPorts =
        {
            FirstUnusedPort + 3, FirstUnusedPort + 42, FirstUnusedPort + 100, FirstUnusedPort + 142
        };

        [Fact]
        public void DoesNotRentReservedPorts()
        {
            Socket[] mockSystemSockets =
            {
                new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
                new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp),
                new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp),
                new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp),
            };

            try
            {
                // Occupy the mock system ports before starting the test process:
                for (int i = 0; i < 4; i++)
                {
                    Socket s = mockSystemSockets[i];
                    IPAddress ip = s.AddressFamily == AddressFamily.InterNetwork
                        ? IPAddress.Loopback
                        : IPAddress.IPv6Loopback;
                    int port = s_mockSystemPorts[i];
                    s.Bind(new IPEndPoint(ip, port));
                }

                // Run the external process:
                RemoteInvokeOptions options = CreateRemoteOptions("25010 25200");
                RemoteExecutor.Invoke(RunTest, options).Dispose();
            }
            finally
            {
                // Unbind and release all sockets:
                foreach (Socket socket in mockSystemSockets)
                {
                    socket.Dispose();
                }
            }

            static void RunTest()
            {
                for (int i = 0; i < 200; i++)
                {
                    using PortLease lease = TestPortPool.RentPort();
                    Assert.DoesNotContain(lease.Port, s_mockSystemPorts);
                }
            }
        }
    }
}
