#if !DISABLE_DHT
using System;
using System.Collections.Generic;
using NUnit.Framework;
using System.Net;
using MonoTorrent.Dht.Tasks;
using MonoTorrent.Dht.Messages;
using MonoTorrent.BEncoding;
using System.Threading;

namespace MonoTorrent.Dht
{
    [TestFixture]
    public class TaskTests
    {
        //static void Main(string[] args)
        //{
        //    TaskTests t = new TaskTests();
        //    t.Setup();
        //    t.ReplaceNodeTest();
        //}

        private DhtEngine _engine;
        private TestListener _listener;
        private Node _node;
        private readonly BEncodedString _transactionId = "aa";
        private ManualResetEvent _handle;
        private int _counter;

        [SetUp]
        public void Setup()
        {
            _counter = 0;
            _listener = new TestListener();
            _engine = new DhtEngine(_listener);
            _node = new Node(NodeId.Create(), new IPEndPoint(IPAddress.Any, 4));
            _handle = new ManualResetEvent(false);
        }

        [Test]
        public void SendQueryTaskTimeout()
        {
            _engine.TimeOut = TimeSpan.FromMilliseconds(25);

            var ping = new Ping(_engine.LocalId) {TransactionId = _transactionId};
            _engine.MessageLoop.QuerySent += (o, e) =>
                                                 {
                                                     if (e.TimedOut)
                                                         _counter++;
                                                 };

            var task = new SendQueryTask(_engine, ping, _node);
            task.Completed += (sender, args) => _handle.Set();
            task.Execute();
            Assert.IsTrue(_handle.WaitOne(3000, false), "#1");
            Assert.AreEqual(task.Retries, _counter);
        }

        [Test]
        public void SendQueryTaskSucceed()
        {
            _engine.TimeOut = TimeSpan.FromMilliseconds(25);

            var ping = new Ping(_engine.LocalId) {TransactionId = _transactionId};
            _engine.MessageLoop.QuerySent += delegate(object o, SendQueryEventArgs e)
            {
                if (!e.TimedOut) 
                    return;

                _counter++;
                var response = new PingResponse(_node.Id, _transactionId);
                _listener.RaiseMessageReceived(response, _node.EndPoint);
            };

            var task = new SendQueryTask(_engine, ping, _node);
            task.Completed += delegate { _handle.Set(); };
            task.Execute();

            Assert.IsTrue(_handle.WaitOne(3000, false), "#1");
            Thread.Sleep(200);
            Assert.AreEqual(1, _counter, "#2");
            var n = _engine.RoutingTable.FindNode(_node.Id);
            Assert.IsNotNull(n, "#3");
            Assert.IsTrue(n.LastSeen > DateTime.UtcNow.AddSeconds(-2));
        }

        int nodeCount = 0;
        [Test]
        public void NodeReplaceTest()
        {
            _engine.TimeOut = TimeSpan.FromMilliseconds(25);
            ManualResetEvent handle = new ManualResetEvent(false);
            Bucket b = new Bucket();
            for (int i = 0; i < Bucket.MaxCapacity; i++)
            {
                Node n = new Node(NodeId.Create(), new IPEndPoint(IPAddress.Any, i));
                n.LastSeen = DateTime.UtcNow;
                b.Add(n);
            }

            b.Nodes[3].LastSeen = DateTime.UtcNow.AddDays(-5);
            b.Nodes[1].LastSeen = DateTime.UtcNow.AddDays(-4);
            b.Nodes[5].LastSeen = DateTime.UtcNow.AddDays(-3);

            _engine.MessageLoop.QuerySent += delegate(object o, SendQueryEventArgs e)
            {
                if (!e.TimedOut)
                    return;

                b.Nodes.Sort();
                if ((e.EndPoint.Port == 3 && nodeCount == 0) ||
                     (e.EndPoint.Port == 1 && nodeCount == 1) ||
                     (e.EndPoint.Port == 5 && nodeCount == 2))
                {
                    Node n = b.Nodes.Find(delegate(Node no) { return no.EndPoint.Port == e.EndPoint.Port; });
                    n.Seen();
                    PingResponse response = new PingResponse(n.Id, e.Query.TransactionId);
                    DhtEngine.MainLoop.Queue(delegate
                    {
                        //System.Threading.Thread.Sleep(100);
                        Console.WriteLine("Faking the receive");
                        _listener.RaiseMessageReceived(response, _node.EndPoint);
                    });
                    nodeCount++;
                }

            };

            ReplaceNodeTask task = new ReplaceNodeTask(_engine, b, null);
            // FIXME: Need to assert that node 0.0.0.0:0 is the one which failed - i.e. it should be replaced
            task.Completed += delegate(object o, TaskCompleteEventArgs e) { handle.Set(); };
            task.Execute();

            Assert.IsTrue(handle.WaitOne(4000, false), "#10");
        }

        [Test]
        [Ignore("Important test, but could not be runned under CI server environment")]
        public void BucketRefreshTest()
        {
            var nodes = new List<Node>();
            for (var i = 0; i < 5; i++)
                nodes.Add(new Node(NodeId.Create(), new IPEndPoint(IPAddress.Any, i)));

            _engine.TimeOut = TimeSpan.FromMilliseconds(25);
            _engine.BucketRefreshTimeout = TimeSpan.FromMilliseconds(75);
            _engine.MessageLoop.QuerySent += delegate(object o, SendQueryEventArgs e)
            {
                DhtEngine.MainLoop.Queue(delegate
                {
                    if (!e.TimedOut)
                        return;

                    var current = nodes.Find(n => n.EndPoint.Port.Equals(e.EndPoint.Port));
                    if (current == null)
                        return;

                    if (e.Query is Ping)
                    {
                        var r = new PingResponse(current.Id, e.Query.TransactionId);
                        _listener.RaiseMessageReceived(r, current.EndPoint);
                    }
                    else if (e.Query is FindNode)
                    {
                        var response = new FindNodeResponse(current.Id, e.Query.TransactionId) {Nodes = ""};
                        _listener.RaiseMessageReceived(response, current.EndPoint);
                    }
                });
            };

            _engine.Add(nodes);
            _engine.Start();

            Thread.Sleep(500);
            foreach (var b in _engine.RoutingTable.Buckets)
            {
                Assert.IsTrue(b.LastChanged > DateTime.UtcNow.AddSeconds(-2));
                Assert.IsTrue(b.Nodes.Exists(n => n.LastSeen > DateTime.UtcNow.AddMilliseconds(-900)));
            }
        }

        [Test]
        public void ReplaceNodeTest()
        {
            _engine.TimeOut = TimeSpan.FromMilliseconds(25);
            var replacement = new Node(NodeId.Create(), new IPEndPoint(IPAddress.Loopback, 1337));
            for(var i=0; i < 4; i++)
            {
                var node = new Node(NodeId.Create(), new IPEndPoint(IPAddress.Any, i))
                               {LastSeen = DateTime.UtcNow.AddMinutes(-i)};
                _engine.RoutingTable.Add(node);
            }
            var nodeToReplace = _engine.RoutingTable.Buckets[0].Nodes[3];

            var task = new ReplaceNodeTask(_engine, _engine.RoutingTable.Buckets[0], replacement);
            task.Completed += (sender, args) => _handle.Set();
            task.Execute();
            Assert.IsTrue(_handle.WaitOne(1000, true), "#a");
            Assert.IsFalse(_engine.RoutingTable.Buckets[0].Nodes.Contains(nodeToReplace), "#1");
            Assert.IsTrue(_engine.RoutingTable.Buckets[0].Nodes.Contains(replacement), "#2");
        }
    }
}
#endif