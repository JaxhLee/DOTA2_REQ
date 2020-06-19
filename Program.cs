using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.Dota.Internal;
using SteamKit2.Internal;
using System;
using System.Collections.Generic;
using System.Threading;

namespace DOTA2_REQ
{
    class LoginInfo
    {
        public string user, pass;

        public string pass_key;
        public ulong custom_game_mode = 1613886175;
        public string custom_map_name = "ranked_1x8";
        public string game_name = "ranked_1x8"; // 描述
        public uint custom_min_players = 1;
        public uint custom_max_players = 8;
        public uint game_mode = (uint)DOTA_GameMode.DOTA_GAMEMODE_CUSTOM;
        public uint server_region = 12;
    }

    class DotaClient
    {
        SteamClient client;

        SteamUser user;
        SteamGameCoordinator gameCoordinator;

        CallbackManager callbackMgr;

        LoginInfo loginInfo;

        bool wait;
        int waitTime = 1;

        bool checkCreateLobby = false;
        int lastCheckTime = -1;
        int checkTime = 5;

        public CMsgDOTAMatch Match { get; private set; }


        // dota2's appid
        const int APPID = 570;


        public DotaClient(LoginInfo loginInfo)
        {
            this.loginInfo = loginInfo;

            client = new SteamClient();

            // get our handlers
            user = client.GetHandler<SteamUser>();
            gameCoordinator = client.GetHandler<SteamGameCoordinator>();

            // setup callbacks
            callbackMgr = new CallbackManager(client);

            callbackMgr.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            callbackMgr.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            callbackMgr.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            callbackMgr.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            callbackMgr.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);
        }


        public void Connect()
        {
            Console.WriteLine("Connecting to Steam...");

            // begin the connection to steam
            client.Connect();
        }



        public void Wait()
        {
            while (!wait)
            {
                if (checkCreateLobby)
                {
                    if (lastCheckTime < 0)
                    {
                        lastCheckTime = DateTime.Now.Second;
                    }
                    int interval = Math.Abs(lastCheckTime - DateTime.Now.Second);
                    if (interval > 0 && interval < 3)
                    {
                        checkTime -= interval;
                        if (checkTime < 1)
                        {
                            lastCheckTime = -1;
                            SendCreateLobby(true);
                            return;
                        }
                    }
                    lastCheckTime = DateTime.Now.Second;
                }
                Console.WriteLine(DateTime.Now.Second);


                // continue running callbacks until we get match details
                callbackMgr.RunWaitCallbacks(TimeSpan.FromSeconds(waitTime));
            }
        }

        // called when the client successfully (or unsuccessfully) connects to steam
        void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Console.WriteLine("Connected! Logging '{0}' into Steam...", loginInfo.user);

            // we've successfully connected, so now attempt to logon
            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = loginInfo.user,
                Password = loginInfo.pass,
            });
        }

        public void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Console.WriteLine("Disconnected from Steam");

            wait = false;
            checkCreateLobby = false;
            lastCheckTime = -1;
            checkTime = 5;

            Console.WriteLine("Try reconnect.");
            
            Connect();
            Wait();
        }

        // called when the client successfully (or unsuccessfully) logs onto an account
        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                // logon failed (password incorrect, steamguard enabled, etc)
                // an EResult of AccountLogonDenied means the account has SteamGuard enabled and an email containing the authcode was sent
                // in that case, you would get the auth code from the email and provide it in the LogOnDetails

                Console.WriteLine("Unable to logon to Steam: {0}", callback.Result);

                wait = true; // we didn't actually get the match details, but we need to jump out of the callback loop
                return;
            }

            Console.WriteLine("Logged in! Launching DOTA...");

            // we've logged into the account
            // now we need to inform the steam server that we're playing dota (in order to receive GC messages)

            // steamkit doesn't expose the "play game" message through any handler, so we'll just send the message manually
            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

            playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = new GameID(APPID), // or game_id = APPID,
            });

            // send it off
            // notice here we're sending this message directly using the SteamClient
            client.Send(playGame);

            // delay a little to give steam some time to establish a GC connection to us
            Thread.Sleep(5000);

            // inform the dota GC that we want a session
            var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);
            clientHello.Body.engine = ESourceEngine.k_ESE_Source2;
            gameCoordinator.Send(clientHello, APPID);
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Logged off of Steam: {0}", callback.Result);
        }

        // called when a gamecoordinator (GC) message arrives
        // these kinds of messages are designed to be game-specific
        // in this case, we'll be handling dota's GC messages
        void OnGCMessage(SteamGameCoordinator.MessageCallback callback)
        {
            // setup our dispatch table for messages
            // this makes the code cleaner and easier to maintain
            var messageMap = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { ( uint )EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                //{ ( uint )EDOTAGCMsg.k_EMsgGCMatchDetailsResponse, OnMatchDetails },
                { ( uint )EDOTAGCMsg.k_EMsgGCPracticeLobbyCreate, OnLobbyCreate },
            };

            Action<IPacketGCMsg> func;
            if (!messageMap.TryGetValue(callback.EMsg, out func))
            {
                // this will happen when we recieve some GC messages that we're not handling
                // this is okay because we're handling every essential message, and the rest can be ignored
                return;
            }

            func(callback.Message);
        }

        // this message arrives when the GC welcomes a client
        // this happens after telling steam that we launched dota (with the ClientGamesPlayed message)
        // this can also happen after the GC has restarted (due to a crash or new version)
        void OnClientWelcome(IPacketGCMsg packetMsg)
        {
            // in order to get at the contents of the message, we need to create a ClientGCMsgProtobuf from the packet message we recieve
            // note here the difference between ClientGCMsgProtobuf and the ClientMsgProtobuf used when sending ClientGamesPlayed
            // this message is used for the GC, while the other is used for general steam messages
            var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg);

            Console.WriteLine("GC is welcoming us. Version: {0}", msg.Body.version);

            if (!checkCreateLobby)
            {
                SendCreateLobby();
            }

            //Console.WriteLine("Requesting details of match {0}", matchId);

            // at this point, the GC is now ready to accept messages from us
            // so now we'll request the details of the match we're looking for

            //var requestMatch = new ClientGCMsgProtobuf<CMsgGCMatchDetailsRequest>((uint)EDOTAGCMsg.k_EMsgGCMatchDetailsRequest);
            //requestMatch.Body.match_id = matchId;

            //gameCoordinator.Send(requestMatch, APPID);
        }

        void SendCreateLobby(bool reWait = false)
        {
            string pass_key = loginInfo.pass_key;

            ulong custom_game_mode = loginInfo.custom_game_mode;
            string custom_map_name = loginInfo.custom_map_name;
            string game_name = loginInfo.game_name;
            uint custom_min_players = loginInfo.custom_min_players;
            uint custom_max_players = loginInfo.custom_max_players;
            uint game_mode = loginInfo.game_mode;
            uint server_region = loginInfo.server_region;

            uint customGameTimestamp = (uint)(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds());

            uint teams = 1;
            //ulong custom_game_crc = 1; ???

            var details = new CMsgPracticeLobbySetDetails
            {
                server_region = server_region,
                game_mode = game_mode,
                allow_spectating = true,
                cm_pick = DOTA_CM_PICK.DOTA_CM_RANDOM,
                bot_difficulty_radiant = DOTABotDifficulty.BOT_DIFFICULTY_HARD,
                bot_difficulty_dire = DOTABotDifficulty.BOT_DIFFICULTY_HARD,
                game_version = DOTAGameVersion.GAME_VERSION_STABLE,
                dota_tv_delay = LobbyDotaTVDelay.LobbyDotaTV_120,
                lan = false,
                pass_key = pass_key,
                custom_game_mode = custom_game_mode.ToString(),
                custom_map_name = custom_map_name,
                custom_game_id = custom_game_mode,
                custom_min_players = custom_min_players,
                custom_max_players = custom_max_players,
                //lan_host_ping_to_server_region = 12,
                visibility = DOTALobbyVisibility.DOTALobbyVisibility_Public,
                //custom_game_crc = custom_game_crc,
                custom_game_timestamp = customGameTimestamp,
                // 2020-06-19 14:35:17 未定义
                //league_selection_priority_choice = SelectionPriorityType.UNDEFINED,
                //league_non_selection_priority_choice = SelectionPriorityType.UNDEFINED,
                pause_setting = LobbyDotaPauseSetting.LobbyDotaPauseSetting_Limited,
                allchat = false,
                allow_cheats = false,
                custom_difficulty = 0,
                dire_series_wins = 0,
                fill_with_bots = false,
                game_name = game_name,
                intro_mode = false,
                leagueid = 0,
                // 2020-06-19 14:35:33 未定义
                //league_game_id = 0,
                //league_selection_priority_team = 0,
                //league_series_id = 0,
                load_game_id = 0,
                lobby_id = 0,
                penalty_level_dire = 0,
                penalty_level_radiant = 0,
                previous_match_override = 0,
                radiant_series_wins = 0,
                series_type = 0,
                bot_radiant = 0,
                bot_dire = 0,
            };

            for (var i = 0; i < teams; i++)
            {
                details.team_details.Add(new CLobbyTeamDetails()
                {
                    team_name = "",
                    team_tag = "",
                    guild_name = "",
                    guild_tag = "",
                    guild_banner_logo = 0,
                    guild_base_logo = 0,
                    guild_id = 0,
                    guild_logo = 0,
                    is_home_team = false,
                    rank = 0,
                    rank_change = 0,
                    team_banner_logo = 0,
                    team_base_logo = 0,
                    team_complete = false,
                    team_id = 0,
                    team_logo = 0
                });
            }

            var lobbyCreate = new ClientGCMsgProtobuf<CMsgPracticeLobbyCreate>((uint)EDOTAGCMsg.k_EMsgGCPracticeLobbyCreate);
            lobbyCreate.Body.pass_key = pass_key;
            lobbyCreate.Body.search_key = "";
            lobbyCreate.Body.lobby_details = details;
            lobbyCreate.Body.lobby_details.pass_key = pass_key;
            lobbyCreate.Body.lobby_details.visibility = DOTALobbyVisibility.DOTALobbyVisibility_Public;

            gameCoordinator.Send(lobbyCreate, APPID);

            waitTime = 1;
            checkTime = 5;
            checkCreateLobby = true;
            if (reWait)
            {
                Console.WriteLine("Resend create lobby.");
                Wait();
            }
            else
            {
                Console.WriteLine("send create lobby.");
            }
        }

        // this message arrives after we've requested the details for a match
        void OnMatchDetails(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgGCMatchDetailsResponse>(packetMsg);

            EResult result = (EResult)msg.Body.result;
            if (result != EResult.OK)
            {
                Console.WriteLine("Unable to request match details: {0}", result);
            }

            wait = true;
            Match = msg.Body.match;

            // we've got everything we need, we can disconnect from steam now
            client.Disconnect();
        }

        void OnLobbyCreate(IPacketGCMsg packetMsg)
        {
            var msg = packetMsg;
            waitTime = 5;
            checkCreateLobby = false;
        }

        // this is a utility function to transform a uint emsg into a string that can be used to display the name
        static string GetEMsgDisplayString(uint eMsg)
        {
            Type[] eMsgEnums =
            {
                typeof( EGCBaseClientMsg ),
                typeof( EDOTAGCMsg ),
                typeof( EGCBaseMsg ),
                typeof( EGCItemMsg ),
                typeof( ESOMsg ),
            };

            foreach (var enumType in eMsgEnums)
            {
                if (Enum.IsDefined(enumType, (int)eMsg))
                    return Enum.GetName(enumType, (int)eMsg);

            }

            return eMsg.ToString();
        }
    }

    class Program
    {
        public static LoginInfo loginInfo;

        static void Main(string[] args)
        {
            args = new string[9];

            //args[0] = "wingstest1";
            //args[1] = "mimazhenmafan";
            args[0] = "steamtesttest1";
            args[1] = "steamtesttest1";
            args[2] = "1793860985";
            args[3] = "dota_heroes_td_colosseum";
            args[4] = "1";
            args[5] = "4";
            args[6] = "12";
            args[7] = "描述";
            args[8] = "PWD";

            Program.loginInfo = new LoginInfo
            {
                user = args[0],
                pass = args[1],
                custom_game_mode = Convert.ToUInt64(args[2]),
                custom_map_name = args[3],
                custom_min_players = Convert.ToUInt32(args[4]),
                custom_max_players = Convert.ToUInt32(args[5]),
                server_region = Convert.ToUInt32(args[6]),
                game_name = args.Length > 7 ? args[7] : "TTT", // 描述
                pass_key = "123",
                game_mode = (uint)DOTA_GameMode.DOTA_GAMEMODE_CUSTOM,
            };

            var dotaClient = new DotaClient(loginInfo);
            dotaClient.Connect();
            dotaClient.Wait();
        }
    }
}
