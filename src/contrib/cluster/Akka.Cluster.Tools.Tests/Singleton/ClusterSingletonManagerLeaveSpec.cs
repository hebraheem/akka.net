﻿using System;
using Akka.Actor;
using Akka.Cluster.Tools.Singleton;
using Akka.Configuration;
using Akka.MultiNodeTests;
using Akka.Remote.TestKit;
using Xunit;
using System.Linq;

namespace Akka.Cluster.Tools.Tests.Singleton
{
    public class ClusterSingletonManagerLeaveSpecConfig : MultiNodeConfig
    {
        public readonly RoleName First;
        public readonly RoleName Second;
        public readonly RoleName Third;

        public ClusterSingletonManagerLeaveSpecConfig()
        {
            First = Role("first");
            Second = Role("second");
            Third = Role("third");

            CommonConfig = ConfigurationFactory.ParseString(@"
                akka.loglevel = INFO
                akka.actor.provider = ""Akka.Cluster.ClusterActorRefProvider""
                akka.remote.log-remote-lifecycle-events = off
                akka.cluster.auto-down-unreachable-after = off
            ").WithFallback(MultiNodeClusterSpec.ClusterConfig());
        }
    }

    public class ClusterSingletonManagerLeaveNode1 : ClusterSingletonManagerLeaveSpec { }
    public class ClusterSingletonManagerLeaveNode2 : ClusterSingletonManagerLeaveSpec { }
    public class ClusterSingletonManagerLeaveNode3 : ClusterSingletonManagerLeaveSpec { }

    public class ClusterSingletonManagerLeaveSpec : MultiNodeClusterSpec
    {
        public sealed class EchoStarted
        {
            public static readonly EchoStarted Instance = new EchoStarted();

            private EchoStarted()
            {
            }
        }

        public class Echo : ReceiveActor
        {
            private readonly IActorRef _testActorRef;

            public Echo(IActorRef testActorRef)
            {
                _testActorRef = testActorRef;
                ReceiveAny(x => Sender.Tell(Self));
            }

            protected override void PostStop()
            {
                base.PostStop();
                _testActorRef.Tell("stopped");
            }
        }

        private readonly RoleName _first;
        private readonly RoleName _second;
        private readonly RoleName _third;

        private readonly Lazy<IActorRef> _echoProxy;

        public ClusterSingletonManagerLeaveSpec() : this(new ClusterSingletonManagerLeaveSpecConfig())
        {
        }

        public ClusterSingletonManagerLeaveSpec(ClusterSingletonManagerLeaveSpecConfig config) : base(config)
        {
            _first = config.First;
            _second = config.Second;
            _third = config.Third;

            _echoProxy = new Lazy<IActorRef>(() => Sys.ActorOf(ClusterSingletonProxy.Props(
                singletonManagerPath: "/user/echo",
                settings: ClusterSingletonProxySettings.Create(Sys)),
                "echoProxy"));
        }

        private void Join(RoleName from, RoleName to)
        {
            RunOn(() =>
            {
                Cluster.Join(Node(to).Address);
                CreateSingleton();
            }, from);
        }

        private IActorRef CreateSingleton()
        {
            return Sys.ActorOf(ClusterSingletonManager.Props(
                singletonProps: Props.Create(() => new Echo(TestActor)),
                terminationMessage: PoisonPill.Instance,
                settings: ClusterSingletonManagerSettings.Create(Sys)),
                "echo");
        }

        [MultiNodeFact(Skip = "TODO")]
        public void Leaving_ClusterSingletonManager_should_handover_to_new_instance()
        {
            Join(_first, _first);
            RunOn(() =>
            {

            }, _first);
            EnterBarrier("first-active");

            Join(_second, _first);
            Join(_third, _first);
            Within(TimeSpan.FromSeconds(10), () =>
            {
                AwaitAssert(() => Assert.Equal(3, Cluster.ReadView.State.Members.Count(m => m.Status == MemberStatus.Up)));
            });
            EnterBarrier("all-up");

            RunOn(() =>
            {
                Cluster.Leave(Node(_first).Address);
            }, _second);
            RunOn(() =>
            {
                ExpectMsg("stopped", TimeSpan.FromSeconds(10));
            }, _first);
            EnterBarrier("first-stopped");

            RunOn(() =>
            {
                var p = CreateTestProbe();
                var firstAddress = Node(_first).Address;
                p.Within(TimeSpan.FromSeconds(10), () =>
                {
                    p.AwaitAssert(() =>
                    {
                        _echoProxy.Value.Tell("hello2", p.Ref);
                        Assert.NotEqual(firstAddress, p.ExpectMsg<IActorRef>(TimeSpan.FromSeconds(1)).Path.Address);
                    });
                });
            }, _second, _third);
            EnterBarrier("handover-done");
        }
    }
}