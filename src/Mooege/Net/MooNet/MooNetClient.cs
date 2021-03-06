﻿/*
 * Copyright (C) 2011 - 2012 mooege project - http://www.mooege.org
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Google.ProtocolBuffers;
using Google.ProtocolBuffers.Descriptors;
using Mooege.Common.Helpers.Hash;
using Mooege.Common.Logging;
using Mooege.Core.Cryptography.SSL;
using Mooege.Core.MooNet.Accounts;
using Mooege.Core.MooNet.Authentication;
using Mooege.Core.MooNet.Channels;
using Mooege.Core.MooNet.Objects;
using Mooege.Net.GS;
using Mooege.Net.MooNet.Packets;
using Mooege.Net.MooNet.RPC;
using OpenSSL;

namespace Mooege.Net.MooNet
{
    public class MooNetClient : IClient, IRpcChannel
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        /// <summary>
        /// TCP connection.
        /// </summary>
        public IConnection Connection { get; set; }

        /// <summary>
        /// The underlying network stream.
        /// </summary>
        public NetworkStream NetworkStream { get; private set; }

        /// <summary>
        /// The underlying TLS stream.
        /// </summary>
        public SslStream TLSStream { get; private set; }


        /// <summary>
        /// MooNet Layer Stream
        /// </summary>
        public MooNetBuffer IncomingMooNetStream { get; private set; }

        /// <summary>
        /// Logged in gs client if any.
        /// </summary>
        public GameClient InGameClient { get; set; }

        /// <summary>
        /// Account for logged in client.
        /// </summary>
        public Account Account { get; set; }

        /// <summary>
        /// Selected Game Account for logged in client.
        /// </summary>
        //public GameAccount CurrentGameAccount { get; set; }

        /// <summary>
        /// Client exported services dictionary.
        /// </summary>
        public Dictionary<uint, uint> Services { get; private set; }

        /// <summary>
        /// Platform of the client.
        /// </summary>
        public ClientPlatform Platform { get; set; }

        /// <summary>
        /// Locale of the client.
        /// </summary>
        public ClientLocale Locale { get; set; }

        /// <summary>
        /// Resulting error code for the authentication process.
        /// </summary>
        public AuthManager.AuthenticationErrorCodes AuthenticationErrorCode;

        public bool ThumbprintReq = false;
        public bool PasswordReq = false;

        /// <summary>
        /// Callback list for issued client RPCs.
        /// </summary>
        public readonly Dictionary<uint, RPCCallback> RPCCallbacks = new Dictionary<uint, RPCCallback>();

        /// <summary>
        /// Object ID map with local object ID as key and remote object ID as value.
        /// </summary>
        private Dictionary<ulong, ulong> MappedObjects { get; set; }

        /// <summary>
        /// Token counter for RPCs.
        /// </summary>
        private uint _tokenCounter = 0;

        public bool MOTDSent { get; private set; }

        /// <summary>
        /// Listener Id for upcoming rpc.
        /// </summary>
        private ulong _listenerId; // last targeted rpc object.

        public string LoginEmail = "";

        public MooNetClient(IConnection connection)
        {
            this.Platform = ClientPlatform.Unknown;
            this.Locale = ClientLocale.Unknown;
            this.MOTDSent = false;

            this.IncomingMooNetStream = new MooNetBuffer();

            this.Connection = connection;
            if (this.Connection != null)
                this.NetworkStream = new NetworkStream(this.Connection.Socket, true);

            this.Services = new Dictionary<uint, uint>();
            this.Services.Add(0x65446991, 0x0); // connection-service is always bound by default. /raist.
            this.MappedObjects = new Dictionary<ulong, ulong>();
        }

        public bnet.protocol.Identity GetIdentity(bool acct, bool gameacct, bool toon)
        {
            var identityBuilder = bnet.protocol.Identity.CreateBuilder();
            if (acct) identityBuilder.SetAccountId(this.Account.BnetEntityId);
            if (gameacct) identityBuilder.SetGameAccountId(this.Account.CurrentGameAccount.BnetEntityId);
            if (toon && this.Account.CurrentGameAccount.CurrentToon != null)
                Logger.Warn("DEPRECATED: GetIdentity called with toon.");
            return identityBuilder.Build();
        }

        public void AuthenticationComplete()
        {
            var logonResponseBuilder = bnet.protocol.authentication.LogonResult.CreateBuilder();

            if (AuthenticationErrorCode != AuthManager.AuthenticationErrorCodes.None)
            {
                Logger.Info("Authentication failed for {0} because of invalid credentals.", LoginEmail);
                logonResponseBuilder.SetErrorCode(6); //Logon failed, please try again (Error 6)

                this.MakeRPC(() =>
                    bnet.protocol.authentication.AuthenticationClient.CreateStub(this).LogonComplete(null, logonResponseBuilder.Build(), callback => { }));

                return;
            }

            Logger.Info("User {0} authenticated successfuly.", LoginEmail);

            this.EnableEncryption();
        }

        #region rpc-call mechanism

        /// <summary>
        /// Allows you target an RPCObject while issuing a RPC.
        /// </summary>
        /// <param name="targetObject"><see cref="RPCObject"/></param>
        /// <param name="rpc">The rpc action.</param>
        public void MakeTargetedRPC(RPCObject targetObject, Action rpc)
        {
            this._listenerId = this.GetRemoteObjectId(targetObject.DynamicId);
            Logger.Trace("[RPC: {0}] Method: {1} Target: {2} [localId: {3}, remoteId: {4}].", this, rpc.Method,
                         targetObject.ToString(), targetObject.DynamicId, this._listenerId);

            rpc();
        }

        /// <summary>
        /// Allows you target an listener directly while issuing a RPC.
        /// </summary>
        /// <param name="listenerId">The listenerId over client.</param>
        /// <param name="rpc">The rpc action.</param>
        public void MakeRPCWithListenerId(ulong listenerId, Action rpc)
        {
            this._listenerId = listenerId;
            Logger.Trace("[RPC: {0}] Method: {1} Target: (listenerId) {2}.", this, rpc.Method, this._listenerId);

            rpc();
        }

        /// <summary>
        /// Allows you to issue an RPC without targeting any RPCObject/Listener.
        /// </summary>
        /// <param name="rpc">The rpc action.</param>
        public void MakeRPC(Action rpc)
        {
            this._listenerId = 0;
            Logger.Trace("[RPC: {0}] Method: {1} Target: N/A", this, rpc.Method);
            rpc();
        }

        /// <summary>
        /// Makes an RPC over remote client.
        /// </summary>
        /// <param name="method">The method to call.</param>
        /// <param name="controller">The rpc controller.</param>
        /// <param name="request">The request message.</param>
        /// <param name="responsePrototype">The response message.</param>
        /// <param name="done">Action to run when client responds RPC.</param>
        public void CallMethod(MethodDescriptor method, IRpcController controller, IMessage request, IMessage responsePrototype, Action<IMessage> done)
        {
            var serviceName = method.Service.FullName;
            var serviceHash = StringHashHelper.HashIdentity(serviceName);

            if (!this.Services.ContainsKey(serviceHash))
            {
                Logger.Error("Not bound to client service {0} [0x{1}] yet.", serviceName, serviceHash.ToString("X8"));
                return;
            }

            var serviceId = this.Services[serviceHash];
            var token = this._tokenCounter++;

            RPCCallbacks.Add(token, new RPCCallback(done, responsePrototype.WeakToBuilder()));

            var packet = new PacketOut((byte)serviceId, MooNetRouter.GetMethodId(method), (uint)token, this._listenerId, request);
            this.Connection.Send(packet);
        }

        #endregion

        #region object-mapping mechanism for rpc calls

        /// <summary>
        /// Maps a given local objectId to remote one over client.
        /// </summary>
        /// <param name="localObjectId">The local objectId.</param>
        /// <param name="remoteObjectId">The remote objectId over client.</param>
        public void MapLocalObjectID(ulong localObjectId, ulong remoteObjectId)
        {
            try
            {
                this.MappedObjects[localObjectId] = remoteObjectId;
            }
            catch (Exception e)
            {
                Logger.DebugException(e, "MapLocalObjectID()");
            }
        }

        /// <summary>
        /// Unmaps an existing local objectId.
        /// </summary>
        /// <param name="localObjectId"></param>
        public void UnmapLocalObjectId(ulong localObjectId)
        {
            try
            {
                this.MappedObjects.Remove(localObjectId);
            }
            catch (Exception e)
            {
                Logger.DebugException(e, "UnmapLocalObjectID()");
            }
        }

        /// <summary>
        /// Returns the remote objectId for given localObjectId.
        /// </summary>
        /// <param name="localObjectId">The local objectId</param>
        /// <returns>The remoteobjectId</returns>
        public ulong GetRemoteObjectId(ulong localObjectId)
        {
            return localObjectId != 0 ? this.MappedObjects[localObjectId] : 0;
        }

        #endregion

        #region TLS support

        // D3 uses TLS 1.0 ( 0x16 0x3 0x1 ) (http://en.wikipedia.org/wiki/Transport_Layer_Security) with following ciphers;
        // * Cipher Suite: TLS_PSK_WITH_AES_256_CBC_SHA (0x008d)
        // * Cipher Suite: TLS_PSK_WITH_3DES_EDE_CBC_SHA (0x008b)
        // * Cipher Suite: TLS_PSK_WITH_AES_128_CBC_SHA (0x008c)
        // * Cipher Suite: TLS_PSK_WITH_RC4_128_SHA (0x008a)
        // * Cipher Suite: TLS_EMPTY_RENEGOTIATION_INFO_SCSV (0x00ff)
        // Which Microsoft's or Mono's System.Net.Security implementation does NOT support them (yet?).
        // So we've to instead use openssl over openssl.net (http://openssl-net.sourceforge.net/) wrapper to support them.
        // GNUTls and so the DotGNU Portable.net (http://dotgnu.org/pnet.html) also supports those chippers but openssl-net gives us that kinda cool SSLStream implementation that's seamlessly handles the stuff.
        // Sample SSLStream code: http://msdn.microsoft.com/en-us/library/system.net.security.sslstream.aspx

        public void EnableEncryption()
        {
            // enable the encryption.
            var encryptRequest = bnet.protocol.connection.EncryptRequest.CreateBuilder().Build();
            this.MakeRPC(() => bnet.protocol.connection.ConnectionService.CreateStub(this).Encrypt(null, encryptRequest, callback => StartupTSLHandshake()));
        }

        private void StartupTSLHandshake()
        {
            this.TLSStream = new SslStream(this.NetworkStream, false);

            try
            {
                this.TLSStream.BeginAuthenticateAsServer(CertificateHelper.Certificate, true, null, SslProtocols.Tls, SslStrength.All, false, this.OnTSLAuthentication, this.TLSStream);
            }
            catch (Exception e)
            {
                Logger.FatalException(e, "Certificate exception: ");
            }
        }

        void OnTSLAuthentication(IAsyncResult result)
        {
            try
            {
                this.TLSStream.EndAuthenticateAsServer(result);
            }
            catch (Exception e)
            {
                Logger.FatalException(e, "OnTSLAuthentication() exception: ");
            }

            if (!this.TLSStream.IsAuthenticated) return;

            Logger.Trace("TLSStream: authenticated: {0}, signed: {1}, encrypted: {2}, cipher: {3} cipher-strength: {4}, hash algorithm: {5}, hash-strength: {6}, key-exchange algorithm: {7}, key-exchange strength: {8}, protocol: {9}",
                this.TLSStream.IsAuthenticated,
                this.TLSStream.IsSigned,
                this.TLSStream.IsEncrypted,
                this.TLSStream.CipherAlgorithm, this.TLSStream.CipherStrength,
                this.TLSStream.HashAlgorithm, this.TLSStream.HashStrength,
                this.TLSStream.KeyExchangeAlgorithm, this.TLSStream.KeyExchangeStrength,
                this.TLSStream.SslProtocol);

            if (this.TLSStream.LocalCertificate != null)
                Logger.Trace("Local certificate was issued to {0} by {1} and is valid from {2} until {3}.", this.TLSStream.LocalCertificate.Subject, this.TLSStream.LocalCertificate.Issuer, this.TLSStream.LocalCertificate.NotBefore, this.TLSStream.LocalCertificate.NotAfter);

            Logger.Trace("Sending logon response:");

            var logonResponseBuilder = bnet.protocol.authentication.LogonResult.CreateBuilder();
            logonResponseBuilder.SetAccount(this.Account.BnetEntityId);
            logonResponseBuilder.SetErrorCode(0);
            foreach (var gameAccount in this.Account.GameAccounts)
            {
                logonResponseBuilder.AddGameAccount(gameAccount.BnetEntityId);
            }
            this.MakeRPC(() =>
                bnet.protocol.authentication.AuthenticationClient.CreateStub(this).LogonComplete(null, logonResponseBuilder.Build(), callback => { }));

            Mooege.Core.MooNet.Online.PlayerManager.PlayerConnected(this);

            Mooege.Core.MooNet.Authentication.AuthManager.SendAccountSettings(this);

            //if (this.TLSStream.RemoteCertificate != null) // throws exception too, should be fixed /raist.
            // Logger.Warn("Remote certificate was issued to {0} by {1} and is valid from {2} until {3}.", this.TLSStream.RemoteCertificate.Subject, this.TLSStream.RemoteCertificate.Issuer, this.TLSStream.RemoteCertificate.NotBefore, this.TLSStream.RemoteCertificate.NotAfter);
        }

        #endregion

        #region text-messaging functionality from server to client

        /// <summary>
        /// Sends a whisper from toon itself to toon.
        /// </summary>
        /// <param name="text"></param>
        public void SendServerWhisper(string text)
        {
            if (text.Trim() == string.Empty) return;

            var notification = bnet.protocol.notification.Notification.CreateBuilder()
                .SetTargetId(this.Account.CurrentGameAccount.BnetEntityId)
                .SetType("WHISPER")
                .SetSenderId(this.Account.CurrentGameAccount.BnetEntityId)
                .SetSenderAccountId(this.Account.BnetEntityId)
                .AddAttribute(bnet.protocol.attribute.Attribute.CreateBuilder().SetName("whisper")
                .SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetStringValue(text).Build()).Build()).Build();

            this.MakeRPC(() => bnet.protocol.notification.NotificationListener.CreateStub(this).
                OnNotificationReceived(null, notification, callback => { }));
        }

        #endregion

        #region current channel

        //TODO: Change to list, client can be in multiple channels now.
        public Channel PartyChannel; //Used for all non game related messages
        public Channel GameChannel; //Used for all game related messages

        private Channel _currentChannel;
        public Channel CurrentChannel
        {
            get
            {
                return _currentChannel;
            }
            set
            {
                if (value == null)
                {
                    this.Channels.Remove(this._currentChannel.DynamicId);
                    Logger.Trace("Client removed from CurrentChannel: {0}, setting new CurrentChannel to {1}", this._currentChannel, this.Channels.FirstOrDefault().Value);
                    this._currentChannel = Channels.FirstOrDefault().Value;
                }
                else if (!Channels.ContainsKey(value.DynamicId))
                {
                    this.Channels.Add(value.DynamicId, value);
                    this._currentChannel = value;
                }
                else
                    this._currentChannel = value;
            }
        }

        public Dictionary<ulong, Channel> Channels = new Dictionary<ulong, Channel>();

        #endregion

        #region channel-state changes

        public void SendStateChangeNotification(RPCObject target, bnet.protocol.presence.ChannelState state)
        {
            var channelState = bnet.protocol.channel.ChannelState.CreateBuilder().SetExtension(bnet.protocol.presence.ChannelState.Presence, state);
            var notification = bnet.protocol.channel.UpdateChannelStateNotification.CreateBuilder().SetStateChange(channelState).Build();

            this.MakeTargetedRPC(target, () =>
                bnet.protocol.channel.ChannelSubscriber.CreateStub(this).NotifyUpdateChannelState(null, notification, callback => { }));
        }

        #endregion

        #region MOTD handling

        /// <summary>
        /// Sends server message of the day text.
        /// </summary>
        public void SendMOTD()
        {
            if (this.MOTDSent)
                return;

            var motd = Config.Instance.MOTD.Trim() != string.Empty ? Config.Instance.MOTD : "Missing MOTD text!";

            this.SendServerWhisper(motd);
            this.MOTDSent = true;
        }

        #endregion

        public override string ToString()
        {
            return String.Format("{{ Client: {0} }}", this.Account == null ? "??" : this.Account.Email);
        }

        /// <summary>
        /// Platform enum for clients.
        /// </summary>
        public enum ClientPlatform
        {
            Unknown,
            Invalid,
            Win,
            Mac
        }

        /// <summary>
        /// Locale enum for clients.
        /// </summary>
        public enum ClientLocale
        {
            Unknown,
            Invalid,
            enUS,
            enGB,
            dede
        }
    }
}
