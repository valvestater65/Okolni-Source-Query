﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Okolni.Source.Common;
using Okolni.Source.Common.ByteHelper;
using Okolni.Source.Query.Responses;

namespace Okolni.Source.Query
{
    public class QueryConnection : IQueryConnection
    {
        private UdpClient m_udpClient;
        private IPEndPoint m_endPoint;

        /// <inheritdoc />
        public string Host
        {
            get;
            set;
        } = "127.0.0.1";

        /// <inheritdoc />
        public int Port
        {
            get;
            set;
        } = 27015;

        public QueryConnection()
        {

        }

        /// <inheritdoc />
        public void Connect(int timeoutMiliSec)
        {
            if (string.IsNullOrEmpty(Host))
                throw new ArgumentNullException(nameof(Host), "A Host must be specified");
            if (Port < 0x0 || Port > 0xFFFF)
                throw new ArgumentOutOfRangeException(nameof(Port), "A Valid Port has to be specified (between 0x0000 and 0xFFFF)");

            if (m_udpClient != null || (m_udpClient != null && m_udpClient.Client.Connected))
                return;

            m_udpClient = new UdpClient();
            m_endPoint = new IPEndPoint(IPAddress.Parse(Host), Port);
            m_udpClient.Connect(m_endPoint);
            m_udpClient.Client.SendTimeout = timeoutMiliSec;
            m_udpClient.Client.ReceiveTimeout = timeoutMiliSec;
        }

        /// <inheritdoc />
        public void Connect()
        {
            Connect(5000);
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            m_udpClient?.Dispose();
        }

        private async Task Request(byte[] requestMessage)
        {
            await m_udpClient.SendAsync(requestMessage, requestMessage.Length);
        }



        private async Task<byte[]> ReceiveAsync()
        {
            var newCancellationToken = new CancellationTokenSource();
            newCancellationToken.CancelAfter(m_udpClient.Client.ReceiveTimeout);
            try
            {
                Memory<byte> buffer = new byte[65527];
                var udpClientReceive =
                    await m_udpClient.Client.ReceiveAsync(buffer, SocketFlags.None, newCancellationToken.Token);
                var recvPacket = buffer[..udpClientReceive];
                return recvPacket.ToArray();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Receive took longer then the specified timeout {m_udpClient.Client.RemoteEndPoint}");
            }
        }


        private async Task<byte[]> FetchResponse()
        {
            var response = await ReceiveAsync();
            var byteReader = response.GetByteReader();
            var header = byteReader.GetInt();
            if (header.Equals(Constants.SimpleResponseHeader))
                return byteReader.GetRemaining();
            return await FetchMultiPacketResponse(byteReader);
        }

        private async Task<byte[]> FetchMultiPacketResponse(IByteReader byteReader)
        {
            var firstResponse = new MultiPacketResponse { Id = byteReader.GetInt(), Total = byteReader.GetByte(), Number = byteReader.GetByte(), Size = byteReader.GetShort(), Payload = byteReader.GetRemaining() };

            var compressed = (firstResponse.Id & 2147483648) == 2147483648; // Check for compression

            var responses = new List<MultiPacketResponse>(new[] { firstResponse });
            for (int i = 1; i < firstResponse.Total; i++)
            {
                var response = m_udpClient.Receive(ref m_endPoint);
                var multiResponseByteReader = response.GetByteReader();
                var header = multiResponseByteReader.GetInt();
                if (header != Constants.MultiPacketResponseHeader)
                {
                    i--;
                    continue;
                }
                var id = multiResponseByteReader.GetInt();
                if (id != firstResponse.Id)
                {
                    i--;
                    continue;
                }
                responses.Add(new MultiPacketResponse { Id = id, Total = multiResponseByteReader.GetByte(), Number = multiResponseByteReader.GetByte(), Size = multiResponseByteReader.GetShort(), Payload = multiResponseByteReader.GetRemaining() });
            }

            var assembledPayload = AssembleResponses(responses);
            var assembledPayloadByteReader = assembledPayload.GetByteReader();

            if (compressed)
                throw new NotImplementedException("Compressed responses are not yet implemented");

            var payloadHeader = assembledPayloadByteReader.GetUInt();

            return assembledPayloadByteReader.GetRemaining();
        }

        private byte[] AssembleResponses(IEnumerable<MultiPacketResponse> responses)
        {
            responses = responses.OrderBy(x => x.Number);
            List<byte> assembledPayload = new List<byte>();

            foreach (var response in responses)
            {
                assembledPayload.AddRange(response.Payload);
            }

            return assembledPayload.ToArray();
        }


        /// <summary>
        /// Gets the servers general information
        /// </summary>
        /// <returns>InfoResponse containing all Infos</returns>
        /// <exception cref="SourceQueryException"></exception>
        public InfoResponse GetInfo(int maxRetries = 10) => GetInfoAsync().GetAwaiter().GetResult();


        /// <summary>
        /// Gets the servers general information
        /// </summary>
        /// <returns>InfoResponse containing all Infos</returns>
        /// <exception cref="SourceQueryException"></exception>
        public async Task<InfoResponse> GetInfoAsync(int maxRetries = 10)
        {
            try
            {
                var requestData = await RequestDataFromServer(Constants.A2S_INFO_REQUEST, maxRetries);

                var byteReader = requestData.reader;
                var header = requestData.header;

                if (header == Constants.A2S_INFO_RESPONSE_GOLDSOURCE)
                    throw new ArgumentException("Obsolete GoldSource Response are not supported right now");


                if (header != Constants.A2S_INFO_RESPONSE)
                    throw new ArgumentException("The fetched Response is no A2S_INFO Response.");

                InfoResponse res = new InfoResponse();

                res.Header = header;
                res.Protocol = byteReader.GetByte();
                res.Name = byteReader.GetString();
                res.Map = byteReader.GetString();
                res.Folder = byteReader.GetString();
                res.Game = byteReader.GetString();
                res.ID = byteReader.GetUShort();
                res.Players = byteReader.GetByte();
                res.MaxPlayers = byteReader.GetByte();
                res.Bots = byteReader.GetByte();
                res.ServerType = byteReader.GetByte().ToServerType();
                res.Environment = byteReader.GetByte().ToEnvironment();
                res.Visibility = byteReader.GetByte().ToVisibility();
                res.VAC = byteReader.GetByte() == 0x01;
                //Check for TheShip
                if (res.ID == 2400)
                {
                    res.Mode = byteReader.GetByte().ToTheShipMode();
                    res.Witnesses = byteReader.GetByte();
                    res.Duration = TimeSpan.FromSeconds(byteReader.GetByte());
                }
                res.Version = byteReader.GetString();

                //IF Has EDF Flag 
                if (byteReader.Remaining > 0)
                {
                    res.EDF = byteReader.GetByte();

                    if ((res.EDF & Constants.EDF_PORT) == Constants.EDF_PORT)
                    {
                        res.Port = byteReader.GetUShort();
                    }
                    if ((res.EDF & Constants.EDF_STEAMID) == Constants.EDF_STEAMID)
                    {
                        res.SteamID = byteReader.GetULong();
                    }
                    if ((res.EDF & Constants.EDF_SOURCETV) == Constants.EDF_SOURCETV)
                    {
                        res.SourceTvPort = byteReader.GetUShort();
                        res.SourceTvName = byteReader.GetString();
                    }
                    if ((res.EDF & Constants.EDF_KEYWORDS) == Constants.EDF_KEYWORDS)
                    {
                        res.KeyWords = byteReader.GetString();
                    }
                    if ((res.EDF & Constants.EDF_GAMEID) == Constants.EDF_GAMEID)
                    {
                        res.GameID = byteReader.GetULong();
                    }
                }

                return res;
            }
            catch (Exception ex)
            {
                throw new SourceQueryException("Could not gather Info", ex);
            }
        }

        /// <summary>
        /// Gets all active players on a server
        /// </summary>
        /// <returns>PlayerResponse containing all players </returns>
        /// <exception cref="SourceQueryException"></exception>
        public PlayerResponse GetPlayers(int maxRetries = 10) => GetPlayersAsync().GetAwaiter().GetResult();



        /// <summary>
        /// Gets all active players on a server
        /// </summary>
        /// <returns>PlayerResponse containing all players </returns>
        /// <exception cref="SourceQueryException"></exception>
        /// 
        public async Task<PlayerResponse> GetPlayersAsync(int maxRetries = 10)
        {
            try
            {
                var requestData = await RequestDataFromServer(Constants.A2S_PLAYER_CHALLENGE_REQUEST, maxRetries, true);

                var byteReader = requestData.reader;
                var header = requestData.header;

                if (!header.Equals(Constants.A2S_PLAYER_RESPONSE))
                    throw new ArgumentException("Response was no player response.");

                PlayerResponse playerResponse = new PlayerResponse() { Header = header, Players = new List<Player>() };
                int playercount = byteReader.GetByte();
                for (int i = 1; i <= playercount; i++)
                {
                    playerResponse.Players.Add(new Player()
                    {
                        Index = byteReader.GetByte(),
                        Name = byteReader.GetString(),
                        Score = byteReader.GetUInt(),
                        Duration = TimeSpan.FromSeconds(byteReader.GetFloat())
                    });
                }

                //IF more bytes == THE SHIP
                if (byteReader.Remaining > 0)
                {
                    playerResponse.IsTheShip = true;
                    for (int i = 0; i < playercount; i++)
                    {
                        playerResponse.Players[i].Deaths = byteReader.GetUInt();
                        playerResponse.Players[i].Money = byteReader.GetUInt();
                    }
                }

                return playerResponse;
            }
            catch (Exception ex)
            {
                throw new SourceQueryException("Could not gather Players", ex);
            }
        }


        /// <summary>
        /// Gets the rules of the server
        /// </summary>
        /// <returns>RuleResponse containing all rules as a Dictionary</returns>
        /// <exception cref="SourceQueryException"></exception>
        public RuleResponse GetRules(int maxRetries = 10) => GetRulesAsync().GetAwaiter().GetResult();


        /// <summary>
        /// Gets the rules of the server
        /// </summary>
        /// <returns>RuleResponse containing all rules as a Dictionary</returns>
        /// <exception cref="SourceQueryException"></exception>
        public async Task<RuleResponse> GetRulesAsync(int maxRetries = 10)
        {
            try
            {
                var requestData = await RequestDataFromServer(Constants.A2S_RULES_CHALLENGE_REQUEST, maxRetries, true);

                var byteReader = requestData.reader;
                var header = requestData.header;

                if (!header.Equals(Constants.A2S_RULES_RESPONSE))
                    throw new ArgumentException("Response was no rules response.");

                RuleResponse ruleResponse = new RuleResponse() { Header = header, Rules = new Dictionary<string, string>() };
                int rulecount = byteReader.GetShort();
                for (int i = 1; i <= rulecount; i++)
                {
                    ruleResponse.Rules.Add(byteReader.GetString(), byteReader.GetString());
                }

                return ruleResponse;
            }
            catch (Exception ex)
            {
                throw new SourceQueryException("Could not gather Rules", ex);
            }
        }

        public async Task<(IByteReader reader, byte header)> RequestDataFromServer(byte[] request, int maxRetries, bool replaceLastBytesInRequest = false)
        {
            await Request(request);
            var response = await FetchResponse();

            var byteReader = response.GetByteReader();
            var header = byteReader.GetByte();

            if (header == Constants.CHALLENGE_RESPONSE) // Header response is a challenge response so the challenge must be sent as well
            {
                var retries = 0;
                do
                {
                    var retryRequest = request;
                    var challenge = byteReader.GetBytes(4);
                    if (replaceLastBytesInRequest)
                        retryRequest.InsertArray(retryRequest.Length - 4, challenge);
                    else
                        Helper.AppendToArray(ref retryRequest, challenge);

                    await Request(retryRequest);

                    var retryResponse = await FetchResponse();
                    byteReader = retryResponse.GetByteReader();
                    header = byteReader.GetByte();

                    retries++;
                } while (header == Constants.CHALLENGE_RESPONSE && retries < maxRetries);

                if (header == Constants.CHALLENGE_RESPONSE)
                    throw new SourceQueryException("Retry limit exceeded for the request.");
            }

            return (byteReader, header);
        }
    }
}
