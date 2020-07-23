﻿ 
using Fenix.Common;
using Fenix.Common.Attributes;
using Fenix.Common.Rpc;
using Fenix.Common.Utils; 
using System;
using System.Collections.Concurrent; 
using System.Linq;
using System.Net;
using System.Reflection;  

namespace Fenix
{
    public class RpcModule
    {
        public static ConcurrentDictionary<UInt32, Api> RpcTypeDic = new ConcurrentDictionary<UInt32, Api>();

        public ConcurrentDictionary<UInt64, RpcCommand> rpcDic     = new ConcurrentDictionary<UInt64, RpcCommand>();
        public ConcurrentDictionary<UInt32, MethodInfo> rpcStubDic = new ConcurrentDictionary<UInt32, MethodInfo>(); 

        public RpcModule()
        {
            var methods = this.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; ++i)
            {
                MethodInfo method = methods[i];
                var attrs = method.GetCustomAttributes(typeof(ServerApiAttribute));
                if (attrs.Count() > 0)
                {
                    uint code = Basic.GenID32FromName(method.Name); 
                    RpcTypeDic[code] = Api.ServerApi;
                }

                attrs = method.GetCustomAttributes(typeof(ServerOnlyAttribute));
                if (attrs.Count() > 0)
                {
                    uint code = Basic.GenID32FromName(method.Name); 
                    RpcTypeDic[code] = Api.ServerOnly;
                }

                attrs = method.GetCustomAttributes(typeof(ClientApiAttribute));
                if (attrs.Count() > 0)
                {
                    uint code = Basic.GenID32FromName(method.Name); 
                    RpcTypeDic[code] = Api.ClientApi;
                }

                attrs = method.GetCustomAttributes(typeof(RpcMethodAttribute));
                if (attrs.Count() > 0)
                {
                    var attr = (RpcMethodAttribute)attrs.First();
                    uint code = attr.Code;
                    Api api = attr.Api;
                    RpcTypeDic[code] = api;
                    rpcStubDic[code] = method;
                }
            }
        }

        public virtual void CallMethod(uint fromHostId, uint toHostId, Packet packet)
        {
            bool isCallback = this.rpcDic.ContainsKey(packet.Id);
            
            if (isCallback)
            {
                var cmd = this.rpcDic[packet.Id];
                this.rpcDic.TryRemove(packet.Id, out var _);
                cmd.Callback(packet.Payload);
            }
            else
            {
                Type type = Global.TypeManager.GetMessageType(packet.ProtoCode);
                IMessage msg = (IMessage)RpcUtil.Deserialize(type, packet.Payload);
                var cmd = RpcCommand.Create(packet.Id, fromHostId, toHostId, packet.FromActorId, packet.ToActorId, packet.ProtoCode, packet.networkType, msg,
                    null,
                    this) ; 
                cmd.Call(()=> {
                    this.rpcDic.TryRemove(cmd.Id, out var _);
                });
            }
        }

        public virtual void CallLocalMethod(uint protoCode, object[] args)
        {
            this.rpcStubDic[protoCode].Invoke(this, args);
        }

        public Api GetRpcType(uint protoCode)
        {
            Api api;
            if (RpcTypeDic.TryGetValue(protoCode, out api))
                return api;

            return Api.NoneApi;
        }

        public void Rpc(uint protoCode, uint fromHostId, uint fromActorId, uint toHostId, uint toActorId, 
            IPEndPoint toPeerAddr, NetworkType netType, IMessage msg, Action<byte[]> cb)
        { 
            //var toHostId = Global.IdManager.GetHostIdByActorId(toActorId); 

            /*创建一个等待回调的rpc_command*/
            var cmd = RpcCommand.Create(
                Basic.GenID64(),
                fromHostId,
                toHostId,
                fromActorId,
                toActorId,
                protoCode,
                netType,
                msg,
                (data) => cb?.Invoke(data),
                this);

            var packet = Packet.Create(cmd.Id, cmd.ProtoCode, cmd.FromHostId, cmd.ToHostId, cmd.FromActorId, cmd.ToActorId, netType, RpcUtil.Serialize(msg));

            //如果是同进程，则本地调用
            if (fromHostId == toHostId)
            {
                var toActor = Host.Instance.GetActor(toActorId);

                if (msg.HasCallback())
                    this.rpcDic[cmd.Id] = cmd; 
                toActor.CallMethod(fromHostId, toHostId, packet);
                return;
            }

            //否则通过网络调用
            var peer = NetManager.Instance.GetPeerById(toHostId);
            //Console.WriteLine(string.Format("{0} {1} {2} {3} {4}", fromHostId, toHostId, fromActorId, toActorId, peer==null?"NULL":""));
            if (peer == null)
            {
                Log.Warning(string.Format("Rpc:cannot_find_peer_and_create {0} => {1}", fromHostId, toHostId));
                peer = NetManager.Instance.CreatePeer(toHostId, toPeerAddr, netType);
            }
  
            if (peer == null || !peer.IsActive)
            {
                Log.Error(string.Format("Rpc:peer disconnected {0}", toHostId));
                //这里可以尝试把global以及redis状态清空
                NetManager.Instance.RemovePeerId(toHostId);
                return;
            }

            if(msg.HasCallback())
                this.rpcDic[cmd.Id] = cmd; 
            
            peer.Send(packet);
        }

        public void RpcCallback(ulong protoId, uint protoCode, uint fromHostId, uint toHostId, uint fromActorId, uint toActorId, NetworkType netType, object cbMsg)
        { 
            //var toHostId = Global.IdManager.GetHostIdByActorId(toActorId);
            //var fromHostId = Global.IdManager.GetHostIdByActorId(fromActorId);

            var packet = Packet.Create(protoId, protoCode, fromHostId, toHostId, fromActorId, toActorId, netType, RpcUtil.Serialize(cbMsg));

            //如果是同进程，则本地调用
            if (fromHostId == toHostId)
            {
                var toActor = Host.Instance.GetActor(toActorId);
                toActor.CallMethod(fromHostId, toHostId, packet);
                return;
            }

            //否则通过网络调用
            var peer = NetManager.Instance.GetPeerById(toHostId);

            if (peer == null)
            {
                Log.Warning(string.Format("RpcCallback:cannot_find_peer_and_create {0} => {1}", fromHostId, toHostId));
                peer = NetManager.Instance.CreatePeer(toHostId, null, netType);
            }

            if (peer == null || !peer.IsActive)
            {
                Log.Error(string.Format("RpcCallback:peer disconnected {0}", toHostId));
                //这里可以尝试把global以及redis状态清空
                NetManager.Instance.RemovePeerId(toHostId);
                return;
            }

            //Console.WriteLine(string.Format("{0} {1} {2} {3} {4}", Host.Instance.Id, toHostId,
            //    Global.TypeManager.GetActorType(fromActorId).Name, Global.TypeManager.GetActorType(toActorId).Name,
            //    peer == null ? "NULL" : ""));

            peer.Send(packet);
        }
    }
}