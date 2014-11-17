﻿using NLog.Internal;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using MT4CliWrapper;
using NLog;
using Newtonsoft.Json;
using Castle.Zmq;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace MT4Proxy.NET.Core
{
    class ZmqServer : IServer, IDisposable
    {
        private static Context _zmqCtx = null;
        private static ConcurrentDictionary<string, Type> _apiDict = new ConcurrentDictionary<string, Type>();
        private static IZmqSocket _publisher = null;
        private static Semaphore _pubSignal = new Semaphore(0, 20000);
        private static ConcurrentQueue<Tuple<string, string>>
            _queMessages = new ConcurrentQueue<Tuple<string, string>>();

        public ZmqServer()
        {
            MT4 = Poll.New();
        }

        internal static void PubMessage(string aTopic, string aMessage)
        {
            var socket = _publisher;
            if(socket != null)
            {
                _queMessages.Enqueue(new Tuple<string, string>(aTopic, aMessage));
                _pubSignal.Release();
            }
        }

        private static void PubProc(object aArg)
        {
            while(MT4Pump.EnableRestart)
            {
                _pubSignal.WaitOne();
                Tuple<string, string> item = null;
                _queMessages.TryDequeue(out item);
                if (item == null) continue;
                var topic = item.Item1;
                var message = item.Item2;
                var socket = _publisher;
                if (socket != null)
                {
                    socket.Send(topic, null, hasMoreToSend: true);
                    socket.Send(message);
                }
            }
        }

        public static void Init()
        {
            Logger initlogger = LogManager.GetLogger("common");
            var config = new ConfigurationManager();
            if(!bool.Parse(config.AppSettings["enable_zmq"]))
            {
                initlogger.Info("ZMQ服务被禁用，跳过ZMQ初始化");
                return;
            }
            _zmqCtx = new Context();
            var zmqBind = config.AppSettings["zmq_bind"];
            foreach (var i in Utils.GetTypesWithServiceAttribute())
            {
                var attr = i.GetCustomAttribute(typeof(MT4ServiceAttribute)) as MT4ServiceAttribute;
                var service = i;
                if (attr.EnableZMQ)
                {
                    if (!string.IsNullOrWhiteSpace(attr.ZmqApiName))
                    {
                        initlogger.Info(string.Format("准备初始化ZMQ服务:{0}", attr.ZmqApiName));
                        _apiDict[attr.ZmqApiName] = i;
                    }
                    else
                    {
                        initlogger.Info(string.Format("准备初始化ZMQ服务:{0}", service.Name));
                        _apiDict[service.Name] = i;
                    }
                }
            }
            var repSocket = _zmqCtx.CreateSocket(SocketType.Rep);
            var pubSocket = _zmqCtx.CreateSocket(SocketType.Pub);
            repSocket.Bind(zmqBind);
            pubSocket.Bind(config.AppSettings["zmq_pub_bind"]);
            _publisher = pubSocket;
            var th_pub = new Thread(PubProc);
            th_pub.IsBackground = true;
            th_pub.Start();
            var jss = new JavaScriptSerializer();
            var polling = new Polling(PollingEvents.RecvReady, repSocket);
            polling.RecvReady += (socket) =>
            {
                try
                {
                    var item = socket.RecvString(Encoding.UTF8);
                    var dict = jss.Deserialize<dynamic>(item);
                    string api_name = dict["__api"];
                    if(_apiDict.ContainsKey(api_name))
                    {
                        var service = _apiDict[api_name];
                        var serviceobj = Activator.CreateInstance(service) as IService;
                        using (var server = new ZmqServer())
                        {
                            server.Logger = LogManager.GetLogger("common");
                            server.Logger.Info(string.Format("ZMQ,recv request:{0}", item));
                            serviceobj.OnRequest(server, dict);
                            if (server.Output != null)
                            {
                                server.Logger.Info(string.Format("ZMQ,response:{0}", server.Output));
                                socket.Send(server.Output);
                            }
                            else
                            {
                                server.Logger.Warn(string.Format("ZMQ,response empty,source:{0}", item));
                                socket.Send(string.Empty);
                            }
                        }
                    }
                }
                catch(Exception e)
                {
                    Logger logger = LogManager.GetLogger("clr_error");
                    logger.Error("处理单个ZMQ请求失败", e.StackTrace);
                    socket.Send(string.Empty);
                }
                finally
                {
                    Task.Factory.StartNew(() => { polling.PollForever(); });
                }
            };
            Task.Factory.StartNew(() => { polling.PollForever(); });
        }

        public void Pub(string aChannel, string aJson)
        {

        }

        public string Output
        {
            get;
            set;
        }

        public MT4Wrapper MT4
        {
            get;
            private set;
        }
        public Logger Logger
        {
            get;
            private set;
        }
        public string RedisOutputList
        {
            get;
            set;
        }

        bool disposed = false;
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                Poll.Release(MT4);
                MT4 = null;
                Logger = null;
            }
            disposed = true;
        }
    }
}
