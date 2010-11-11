/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

using log4net;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Console;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using OpenSim.Services.Connectors.Hypergrid;
using Aurora.DataManager;
using Aurora.Framework;

namespace OpenSim.Services.LLLoginService
{
    public interface ILoginModule
    {
        void Initialize(LLLoginService service, IConfigSource source, IUserAccountService UAService);
        bool Login(Hashtable request, UUID User, out string message);
    }

    public class LLLoginService : ILoginService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static bool Initialized = false;

        protected IUserAccountService m_UserAccountService;
        protected IGridUserService m_GridUserService;
        protected IAuthenticationService m_AuthenticationService;
        protected IInventoryService m_InventoryService;
        protected IGridService m_GridService;
        protected IPresenceService m_PresenceService;
        protected ISimulationService m_LocalSimulationService;
        protected ISimulationService m_RemoteSimulationService;
        protected ILibraryService m_LibraryService;
        protected IFriendsService m_FriendsService;
        protected IAvatarService m_AvatarService;
        protected IUserAgentService m_UserAgentService;

        protected GatekeeperServiceConnector m_GatekeeperConnector;

        protected string m_DefaultRegionName;
        protected string m_WelcomeMessage;
        protected bool m_RequireInventory;
        protected int m_MinLoginLevel;
        protected string m_GatekeeperURL;
        protected bool m_AllowRemoteSetLoginLevel;
        protected string m_MapTileURL;
        protected string m_SearchURL;

        IConfig m_LoginServerConfig;
        IConfigSource m_config;
        IConfig m_AuroraLoginConfig;
        bool m_AllowAnonymousLogin = false;
        bool m_AuthenticateUsers = true;
        bool m_UseTOS = false;
        string m_TOSLocation = "";
        string m_DefaultUserAvatarArchive = "";
        string m_DefaultHomeRegion = "";
        bool m_AllowFirstLife = true;
        string m_TutorialURL = "";
        ArrayList eventCategories = new ArrayList();
        ArrayList classifiedCategories = new ArrayList();
        string CAPSServerURL = "";
        string CAPSServicePassword = "";
        GridAvatarArchiver archiver;
        List<ILoginModule> LoginModules = new List<ILoginModule>();
        bool allowExportPermission = true;

        public LLLoginService(IConfigSource config, ISimulationService simService, ILibraryService libraryService)
        {
            m_config = config;
            m_AuroraLoginConfig = config.Configs["AuroraLoginService"];
            if (m_AuroraLoginConfig != null)
            {
                m_UseTOS = m_AuroraLoginConfig.GetBoolean("UseTermsOfServiceOnFirstLogin", false);
                m_DefaultHomeRegion = m_AuroraLoginConfig.GetString("DefaultHomeRegion", "");
                m_DefaultUserAvatarArchive = m_AuroraLoginConfig.GetString("DefaultAvatarArchiveForNewUser", "");
                m_AllowAnonymousLogin = m_AuroraLoginConfig.GetBoolean("AllowAnonymousLogin", false);
                m_AuthenticateUsers = m_AuroraLoginConfig.GetBoolean("AuthenticateUsers", true);
                m_TOSLocation = m_AuroraLoginConfig.GetString("FileNameOfTOS", "");
                m_AllowFirstLife = m_AuroraLoginConfig.GetBoolean("AllowFirstLifeInProfile", true);
                m_TutorialURL = m_AuroraLoginConfig.GetString("TutorialURL", m_TutorialURL);
                ReadEventValues(m_AuroraLoginConfig);
                ReadClassifiedValues(m_AuroraLoginConfig);
                CAPSServerURL = m_AuroraLoginConfig.GetString("CAPSServiceURL", "");
                CAPSServicePassword = m_AuroraLoginConfig.GetString("CAPSServicePassword", "");
                allowExportPermission = m_AuroraLoginConfig.GetBoolean("AllowUseageOfExportPermissions", true);
            }

            m_LoginServerConfig = config.Configs["LoginService"];
            if (m_LoginServerConfig == null)
                throw new Exception(String.Format("No section LoginService in config file"));

            string accountService = m_LoginServerConfig.GetString("UserAccountService", String.Empty);
            string gridUserService = m_LoginServerConfig.GetString("GridUserService", String.Empty);
            string agentService = m_LoginServerConfig.GetString("UserAgentService", String.Empty);
            string authService = m_LoginServerConfig.GetString("AuthenticationService", String.Empty);
            string invService = m_LoginServerConfig.GetString("InventoryService", String.Empty);
            string gridService = m_LoginServerConfig.GetString("GridService", String.Empty);
            string presenceService = m_LoginServerConfig.GetString("PresenceService", String.Empty);
            string libService = m_LoginServerConfig.GetString("LibraryService", String.Empty);
            string friendsService = m_LoginServerConfig.GetString("FriendsService", String.Empty);
            string avatarService = m_LoginServerConfig.GetString("AvatarService", String.Empty);
            string simulationService = m_LoginServerConfig.GetString("SimulationService", String.Empty);

            m_DefaultRegionName = m_LoginServerConfig.GetString("DefaultRegion", String.Empty);
            m_WelcomeMessage = m_LoginServerConfig.GetString("WelcomeMessage", "Welcome to OpenSim!");
            m_RequireInventory = m_LoginServerConfig.GetBoolean("RequireInventory", true);
            m_AllowRemoteSetLoginLevel = m_LoginServerConfig.GetBoolean("AllowRemoteSetLoginLevel", false);
            m_MinLoginLevel = m_LoginServerConfig.GetInt("MinLoginLevel", 0);
            m_GatekeeperURL = m_LoginServerConfig.GetString("GatekeeperURI", string.Empty);
            m_MapTileURL = m_LoginServerConfig.GetString("MapTileURL", string.Empty);
            m_SearchURL = m_LoginServerConfig.GetString("SearchURL", string.Empty);
            
            // These are required; the others aren't
            if (accountService == string.Empty || authService == string.Empty)
                throw new Exception("LoginService is missing service specifications");

            Object[] args = new Object[] { config };
            m_UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(accountService, args);
            m_GridUserService = ServerUtils.LoadPlugin<IGridUserService>(gridUserService, args);
            m_AuthenticationService = ServerUtils.LoadPlugin<IAuthenticationService>(authService, args);
            m_InventoryService = ServerUtils.LoadPlugin<IInventoryService>(invService, args);

            if (gridService != string.Empty)
                m_GridService = ServerUtils.LoadPlugin<IGridService>(gridService, args);
            if (presenceService != string.Empty)
                m_PresenceService = ServerUtils.LoadPlugin<IPresenceService>(presenceService, args);
            if (avatarService != string.Empty)
                m_AvatarService = ServerUtils.LoadPlugin<IAvatarService>(avatarService, args);
            if (friendsService != string.Empty)
                m_FriendsService = ServerUtils.LoadPlugin<IFriendsService>(friendsService, args);
            if (simulationService != string.Empty)
                m_RemoteSimulationService = ServerUtils.LoadPlugin<ISimulationService>(simulationService, args);
            if (agentService != string.Empty)
                m_UserAgentService = ServerUtils.LoadPlugin<IUserAgentService>(agentService, args);

            //
            // deal with the services given as argument
            //
            m_LocalSimulationService = simService;
            if (libraryService != null)
            {
                m_log.DebugFormat("[LLOGIN SERVICE]: Using LibraryService given as argument");
                m_LibraryService = libraryService;
            }
            else if (libService != string.Empty)
            {
                m_log.DebugFormat("[LLOGIN SERVICE]: Using instantiated LibraryService");
                m_LibraryService = ServerUtils.LoadPlugin<ILibraryService>(libService, args);
            }

            m_GatekeeperConnector = new GatekeeperServiceConnector();

            if (!Initialized)
            {
                Initialized = true;
                RegisterCommands();
            }
            //Start the grid profile archiver.
            new GridAvatarProfileArchiver(m_UserAccountService);
            archiver = new GridAvatarArchiver(m_UserAccountService, m_AvatarService, m_InventoryService);
            LoginModules = Aurora.Framework.AuroraModuleLoader.PickupModules<ILoginModule>();
            foreach (ILoginModule module in LoginModules)
            {
                module.Initialize(this, config, m_UserAccountService);
            }

            m_log.DebugFormat("[LLOGIN SERVICE]: Starting...");

        }

        public LLLoginService(IConfigSource config)
            : this(config, null, null)
        {
        }

        public void ReadEventValues(IConfig config)
        {
            SetEventCategories((Int32)DirectoryManager.EventCategories.Discussion, "Discussion");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Sports, "Sports");
            SetEventCategories((Int32)DirectoryManager.EventCategories.LiveMusic, "Live Music");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Commercial, "Commercial");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Nightlife, "Nightlife/Entertainment");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Games, "Games/Contests");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Pageants, "Pageants");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Education, "Education");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Arts, "Arts and Culture");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Charity, "Charity/Support Groups");
            SetEventCategories((Int32)DirectoryManager.EventCategories.Miscellaneous, "Miscellaneous");
        }

        public void ReadClassifiedValues(IConfig config)
        {
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.Shopping, "Shopping");
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.LandRental, "Land Rental");
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.PropertyRental, "Property Rental");
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.SpecialAttraction, "Special Attraction");
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.NewProducts, "New Products");
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.Employment, "Employment");
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.Wanted, "Wanted");
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.Service, "Service");
            AddClassifiedCategory((Int32)DirectoryManager.ClassifiedCategories.Personal, "Personal");
        }

        public void SetEventCategories(Int32 value, string categoryName)
        {
            Hashtable hash = new Hashtable();
            hash["category_name"] = categoryName;
            hash["category_id"] = value;
            eventCategories.Add(hash);
        }

        public void AddClassifiedCategory(Int32 ID, string categoryName)
        {
            Hashtable hash = new Hashtable();
            hash["category_name"] = categoryName;
            hash["category_id"] = ID;
            classifiedCategories.Add(hash);
        }

        public Hashtable SetLevel(string firstName, string lastName, string passwd, int level, IPEndPoint clientIP)
        {
            Hashtable response = new Hashtable();
            response["success"] = "false";

            if (!m_AllowRemoteSetLoginLevel)
                return response;

            try
            {
                UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, firstName, lastName);
                if (account == null)
                {
                    m_log.InfoFormat("[LLOGIN SERVICE]: Set Level failed, user {0} {1} not found", firstName, lastName);
                    return response;
                }

                if (account.UserLevel < 200)
                {
                    m_log.InfoFormat("[LLOGIN SERVICE]: Set Level failed, reason: user level too low");
                    return response;
                }

                //
                // Authenticate this user
                //
                // We don't support clear passwords here
                //
                string token = m_AuthenticationService.Authenticate(account.PrincipalID, passwd, 30);
                UUID secureSession = UUID.Zero;
                if ((token == string.Empty) || (token != string.Empty && !UUID.TryParse(token, out secureSession)))
                {
                    m_log.InfoFormat("[LLOGIN SERVICE]: SetLevel failed, reason: authentication failed");
                    return response;
                }
            }
            catch (Exception e)
            {
                m_log.Error("[LLOGIN SERVICE]: SetLevel failed, exception " + e.ToString());
                return response;
            }

            m_MinLoginLevel = level;
            m_log.InfoFormat("[LLOGIN SERVICE]: Login level set to {0} by {1} {2}", level, firstName, lastName);

            response["success"] = true;
            return response;
        }

        public LoginResponse Login(string firstName, string lastName, string passwd, string startLocation, UUID scopeID, 
            string clientVersion, string channel, string mac, string id0, IPEndPoint clientIP, Hashtable requestData)
        {
            bool success = false;
            UUID session = UUID.Random();

            m_log.InfoFormat("[LLOGIN SERVICE]: Login request for {0} {1} from {2} with user agent {3} starting in {4}", 
                firstName, lastName, clientIP.Address.ToString(), clientVersion, startLocation);
            try
            {
                //
                // Get the account and check that it exists
                //
                UserAccount account = m_UserAccountService.GetUserAccount(scopeID, firstName, lastName);
                if (!passwd.StartsWith("$1$"))
                    passwd = "$1$" + Util.Md5Hash(passwd);
                passwd = passwd.Remove(0, 3); //remove $1$
                if (account == null)
                {
                    if (!m_AllowAnonymousLogin)
                    {
                        m_log.InfoFormat("[LLOGIN SERVICE]: Login failed, reason: user not found");
                        return LLFailedLoginResponse.UserProblem;
                    }
                    else
                    {
                        account = new UserAccount(UUID.Zero, firstName, lastName, "");
                        account.UserTitle = "";
                        m_UserAccountService.StoreUserAccount(account);
                        account = m_UserAccountService.GetUserAccount(scopeID, firstName, lastName);
                        m_AuthenticationService.SetPasswordHashed(account.PrincipalID, passwd);
                        m_InventoryService.CreateUserInventory(account.PrincipalID);
                    }
                }

                UUID secureSession = UUID.Zero;
                if (m_AuthenticateUsers)
                {
                    //
                    // Authenticate this user
                    //
                    string token = m_AuthenticationService.Authenticate(account.PrincipalID, passwd, 30);
                    if ((token == string.Empty) || (token != string.Empty && !UUID.TryParse(token, out secureSession)))
                    {
                        m_log.InfoFormat("[LLOGIN SERVICE]: Login failed, reason: authentication failed");
                        return LLFailedLoginResponse.UserProblem;
                    }
                }
                else
                {
                    string token = m_AuthenticationService.GetToken(account.PrincipalID, 30);
                    UUID.TryParse(token, out secureSession);
                }

                IAgentInfo agent = null;

                IAgentConnector agentData = DataManager.RequestPlugin<IAgentConnector>();
                IProfileConnector profileData = DataManager.RequestPlugin<IProfileConnector>();
                //Already tried to find it before this, so its not there at all.
                if (agentData != null)
                {
                    agent = agentData.GetAgent(account.PrincipalID);
                    if (agent == null)
                    {
                        agentData.CreateNewAgent(account.PrincipalID);
                        agent = agentData.GetAgent(account.PrincipalID);
                    }
                    bool AcceptedNewTOS = false;
                    //This gets if the viewer has accepted the new TOS
                    if(requestData.ContainsKey("agree_to_tos"))
                    {
                        if (requestData["agree_to_tos"].ToString() == "0")
                            AcceptedNewTOS = false;
                        else if (requestData["agree_to_tos"].ToString() == "1")
                            AcceptedNewTOS = true;
                        else
                            AcceptedNewTOS = bool.Parse(requestData["agree_to_tos"].ToString());

                        agent.AcceptTOS = AcceptedNewTOS;
                        agentData.UpdateAgent(agent);
                    }
                    if (!AcceptedNewTOS && !agent.AcceptTOS && m_UseTOS)
                    {
                        StreamReader reader = new StreamReader(Path.Combine(Environment.CurrentDirectory, m_TOSLocation));
                        string TOS = reader.ReadToEnd();
                        reader.Close();
                        reader.Dispose();
                        return new LLFailedLoginResponse(LoginResponseEnum.ToSNeedsSent, TOS, "false");
                    }
                    if ((agent.Flags & IAgentFlags.PermBan) == IAgentFlags.PermBan || (agent.Flags & IAgentFlags.TempBan) == IAgentFlags.TempBan)
                    {
                        m_log.Info("[LLOGIN SERVICE]: Login failed, reason: user is banned.");
                        return new LLFailedLoginResponse(LoginResponseEnum.MessagePopup, "You are blocked from connecting to this service.", "false");
                    }

                    if (account.UserLevel < m_MinLoginLevel)
                    {
                        m_log.InfoFormat("[LLOGIN SERVICE]: Login failed, reason: login is blocked for user level {0}", account.UserLevel);
                        return LLFailedLoginResponse.LoginBlockedProblem;
                    }
                }
                if (profileData != null)
                {
                    IUserProfileInfo UPI = profileData.GetUserProfile(account.PrincipalID);
                    if (UPI == null)
                    {
                        profileData.CreateNewProfile(account.PrincipalID);
                        UPI = profileData.GetUserProfile(account.PrincipalID);
                        UPI.AArchiveName = m_DefaultUserAvatarArchive;
                        //profileData.UpdateUserProfile(UPI); //It gets hit later by the next thing
                    }
                    if (UPI.IsNewUser && UPI.AArchiveName != "" && UPI.AArchiveName != " ")
                    {
                        GridAvatarArchiver archiver = new GridAvatarArchiver(m_UserAccountService, m_AvatarService, m_InventoryService);
                        archiver.LoadAvatarArchive(UPI.AArchiveName, account.FirstName, account.LastName);
                        UPI.AArchiveName = "";
                    }
                    if (UPI.IsNewUser)
                    {
                        UPI.IsNewUser = false;
                        profileData.UpdateUserProfile(UPI);
                    }
                }
                requestData["ip"] = clientIP.ToString();
                foreach (ILoginModule module in LoginModules)
                {
                    string message;
                    if (module.Login(requestData, account.PrincipalID, out message) == false)
                    {
                        LLFailedLoginResponse resp = new LLFailedLoginResponse(LoginResponseEnum.PasswordIncorrect,
                            message, "false");
                        return resp;
                    }
                }

                //
                // Get the user's inventory
                //
                if (m_RequireInventory && m_InventoryService == null)
                {
                    m_log.WarnFormat("[LLOGIN SERVICE]: Login failed, reason: inventory service not set up");
                    return LLFailedLoginResponse.InventoryProblem;
                }
                List<InventoryFolderBase> inventorySkel = m_InventoryService.GetInventorySkeleton(account.PrincipalID);
                if (m_RequireInventory && ((inventorySkel == null) || (inventorySkel != null && inventorySkel.Count == 0)))
                {
                    m_log.InfoFormat("[LLOGIN SERVICE]: Login failed, reason: unable to retrieve user inventory");
                    return LLFailedLoginResponse.InventoryProblem;
                }

                // Get active gestures
                List<InventoryItemBase> gestures = m_InventoryService.GetActiveGestures(account.PrincipalID);
                m_log.DebugFormat("[LLOGIN SERVICE]: {0} active gestures", gestures.Count);

                //
                // Login the presence
                //
                if (m_PresenceService != null)
                {
                    success = m_PresenceService.LoginAgent(account.PrincipalID.ToString(), session, secureSession);
                    if (!success)
                    {
                        m_log.InfoFormat("[LLOGIN SERVICE]: Login failed, reason: could not login presence");
                        return LLFailedLoginResponse.GridProblem;
                    }
                }

                //
                // Change Online status and get the home region
                //
                GridRegion home = null;
                GridUserInfo guinfo = m_GridUserService.LoggedIn(account.PrincipalID.ToString());
                if (guinfo != null && (guinfo.HomeRegionID != UUID.Zero) && m_GridService != null)
                {
                    home = m_GridService.GetRegionByUUID(scopeID, guinfo.HomeRegionID);
                }
                bool GridUserInfoFound = true;
                if (guinfo == null)
                {
                    GridUserInfoFound = false;
                    // something went wrong, make something up, so that we don't have to test this anywhere else
                    guinfo = new GridUserInfo();
                    guinfo.LastPosition = guinfo.HomePosition = new Vector3(128, 128, 30);
                }

                //
                // Find the destination region/grid
                //
                string where = string.Empty;
                Vector3 position = Vector3.Zero;
                Vector3 lookAt = Vector3.Zero;
                GridRegion gatekeeper = null;
                GridRegion destination = FindDestination(account, scopeID, guinfo, session, startLocation, home, out gatekeeper, out where, out position, out lookAt);
                if (destination == null)
                {
                    m_PresenceService.LogoutAgent(session);
                    m_log.InfoFormat("[LLOGIN SERVICE]: Login failed, reason: destination not found");
                    return LLFailedLoginResponse.GridProblem;
                }
                if (!GridUserInfoFound || guinfo.HomeRegionID == UUID.Zero) //Give them a default home and last
                {
                    List<GridRegion> DefaultRegions = m_GridService.GetDefaultRegions(UUID.Zero);
                    GridRegion DefaultRegion = null;
                    if (DefaultRegions.Count == 0)
                        DefaultRegion = destination;
                    else
                        DefaultRegion = DefaultRegions[0];

                    if (m_DefaultHomeRegion != "" && guinfo.HomeRegionID == UUID.Zero)
                    {
                        GridRegion newHomeRegion = m_GridService.GetRegionByName(UUID.Zero, m_DefaultHomeRegion);
                        if (newHomeRegion == null)
                            guinfo.HomeRegionID = guinfo.LastRegionID = DefaultRegion.RegionID;
                        else
                            guinfo.HomeRegionID = guinfo.LastRegionID = newHomeRegion.RegionID;
                    }
                    else if (guinfo.HomeRegionID == UUID.Zero)
                        guinfo.HomeRegionID = guinfo.LastRegionID = DefaultRegion.RegionID;

                    guinfo.LastPosition = guinfo.HomePosition = new Vector3(128, 128, 128);

                    guinfo.HomeLookAt = guinfo.LastLookAt = new Vector3(0, 0, 0);

                    m_GridUserService.SetLastPosition(guinfo.UserID, UUID.Zero, guinfo.LastRegionID, guinfo.LastPosition, guinfo.LastLookAt);
                    m_GridUserService.SetHome(guinfo.UserID, guinfo.HomeRegionID, guinfo.HomePosition, guinfo.HomeLookAt);
                }

                //
                // Get the avatar
                //
                AvatarData avatar = null;
                if (m_AvatarService != null)
                {
                    avatar = m_AvatarService.GetAvatar(account.PrincipalID);
                    if (avatar == null)
                    {
                        m_log.Error("[LLLOGINSERVICE]: CANNOT FIND AVATAR APPEARANCE " + account.FirstName + " " + account.LastName);
                    }
                }

                //
                // Instantiate/get the simulation interface and launch an agent at the destination
                //
                string reason = string.Empty;
                GridRegion dest;
                AgentCircuitData aCircuit = LaunchAgentAtGrid(gatekeeper, destination, account, avatar, session, secureSession, position, where, 
                    clientVersion, channel, mac, id0, clientIP, out where, out reason, out dest);
                destination = dest;
                if(requestData.ContainsKey("id0"))
                    id0 = (string)requestData["id0"];
                string platform = "";
                if(requestData.ContainsKey("platform"))
                    platform = (string)requestData["platform"];

                if (aCircuit == null)
                {
                    m_PresenceService.LogoutAgent(session);
                    m_log.InfoFormat("[LLOGIN SERVICE]: Login failed, reason: {0}", reason);
                    return new LLFailedLoginResponse(LoginResponseEnum.PasswordIncorrect, reason, "false");
                }

                // Get Friends list 
                FriendInfo[] friendsList = new FriendInfo[0];
                if (m_FriendsService != null)
                {
                    friendsList = m_FriendsService.GetFriends(account.PrincipalID);
                    m_log.DebugFormat("[LLOGIN SERVICE]: Retrieved {0} friends", friendsList.Length);
                }

                //
                // Finally, fill out the response and return it
                //
                string MaturityRating = "A";
                string MaxMaturity = "A";
                if (agent != null)
                {
                    if (agent.MaturityRating == 0)
                        MaturityRating = "P";
                    else if (agent.MaturityRating == 1)
                        MaturityRating = "M";
                    else if (agent.MaturityRating == 2)
                        MaturityRating = "A";

                    if (agent.MaxMaturity == 0)
                        MaxMaturity = "P";
                    else if (agent.MaxMaturity == 1)
                        MaxMaturity = "M";
                    else if (agent.MaxMaturity == 2)
                        MaxMaturity = "A";
                }

                LLLoginResponse response = new LLLoginResponse(account, aCircuit, guinfo, destination, inventorySkel, friendsList, m_LibraryService,
                    where, startLocation, position, lookAt, gestures, m_WelcomeMessage, home, clientIP, MaxMaturity, MaturityRating, m_MapTileURL, m_SearchURL,
                    m_AllowFirstLife ? "Y" : "N", m_TutorialURL, eventCategories, classifiedCategories, CAPSServerURL, CAPSServicePassword, allowExportPermission, m_config);

                m_log.DebugFormat("[LLOGIN SERVICE]: All clear. Sending login response to client to login to region " + destination.RegionName + ", tried to login to " + startLocation + " at " + position.ToString() + ".");
                return response;
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[LLOGIN SERVICE]: Exception processing login for {0} {1}: {2} {3}", firstName, lastName, e.ToString(), e.StackTrace);
                if (m_PresenceService != null)
                    m_PresenceService.LogoutAgent(session);
                return LLFailedLoginResponse.InternalError;
            }
        }

        protected GridRegion FindDestination(UserAccount account, UUID scopeID, GridUserInfo pinfo, UUID sessionID, string startLocation, GridRegion home, out GridRegion gatekeeper, out string where, out Vector3 position, out Vector3 lookAt)
        {
            m_log.DebugFormat("[LLOGIN SERVICE]: FindDestination for start location {0}", startLocation);

            gatekeeper = null;
            where = "home";
            position = new Vector3(128, 128, 0);
            lookAt = new Vector3(0, 1, 0);

            if (m_GridService == null)
                return null;

            if (startLocation.Equals("home"))
            {
                // logging into home region
                if (pinfo == null)
                    return null;

                GridRegion region = null;

                bool tryDefaults = false;

                if (home == null)
                {
                    m_log.WarnFormat(
                        "[LLOGIN SERVICE]: User {0} {1} tried to login to a 'home' start location but they have none set",
                        account.FirstName, account.LastName);

                    tryDefaults = true;
                }
                else
                {
                    region = home;

                    position = pinfo.HomePosition;
                    lookAt = pinfo.HomeLookAt;
                }

                if (tryDefaults)
                {
                    List<GridRegion> defaults = m_GridService.GetDefaultRegions(scopeID);
                    if (defaults != null && defaults.Count > 0)
                    {
                        region = defaults[0];
                        where = "safe";
                    }
                    else
                    {
                        m_log.WarnFormat("[LLOGIN SERVICE]: User {0} {1} does not have a valid home and this grid does not have default locations. Attempting to find random region",
                            account.FirstName, account.LastName);
                        defaults = m_GridService.GetRegionsByName(scopeID, "", 1);
                        if (defaults != null && defaults.Count > 0)
                        {
                            region = defaults[0];
                            where = "safe";
                        }
                    }
                }

                return region;
            }
            else if (startLocation.Equals("last"))
            {
                // logging into last visited region
                where = "last";

                if (pinfo == null)
                    return null;

                GridRegion region = null;

                if (pinfo.LastRegionID.Equals(UUID.Zero) || (region = m_GridService.GetRegionByUUID(scopeID, pinfo.LastRegionID)) == null)
                {
                    List<GridRegion> defaults = m_GridService.GetDefaultRegions(scopeID);
                    if (defaults != null && defaults.Count > 0)
                    {
                        region = defaults[0];
                        where = "safe";
                    }
                    else
                    {
                        m_log.Info("[LLOGIN SERVICE]: Last Region Not Found Attempting to find random region");
                        defaults = m_GridService.GetRegionsByName(scopeID, "", 1);
                        if (defaults != null && defaults.Count > 0)
                        {
                            region = defaults[0];
                            where = "safe";
                        }
                    }

                }
                else
                {
                    position = pinfo.LastPosition;
                    lookAt = pinfo.LastLookAt;
                }

                return region;
            }
            else
            {
                // free uri form
                // e.g. New Moon&135&46  New Moon@osgrid.org:8002&153&34
                where = "url";
                Regex reURI = new Regex(@"^uri:(?<region>[^&]+)&(?<x>\d+)&(?<y>\d+)&(?<z>\d+)$");
                Match uriMatch = reURI.Match(startLocation);
                if (uriMatch == null)
                {
                    m_log.InfoFormat("[LLLOGIN SERVICE]: Got Custom Login URI {0}, but can't process it", startLocation);
                    return null;
                }
                else
                {
                    position = new Vector3(float.Parse(uriMatch.Groups["x"].Value, Culture.NumberFormatInfo),
                                           float.Parse(uriMatch.Groups["y"].Value, Culture.NumberFormatInfo),
                                           float.Parse(uriMatch.Groups["z"].Value, Culture.NumberFormatInfo));

                    string regionName = uriMatch.Groups["region"].ToString();
                    if (regionName != null)
                    {
                        if (!regionName.Contains("@"))
                        {
                            List<GridRegion> regions = m_GridService.GetRegionsByName(scopeID, regionName, 1);
                            if ((regions == null) || (regions != null && regions.Count == 0))
                            {
                                m_log.InfoFormat("[LLLOGIN SERVICE]: Got Custom Login URI {0}, can't locate region {1}. Trying defaults.", startLocation, regionName);
                                regions = m_GridService.GetDefaultRegions(scopeID);
                                if (regions != null && regions.Count > 0)
                                {
                                    where = "safe";
                                    return regions[0];
                                }
                                else
                                {
                                    m_log.InfoFormat("[LLLOGIN SERVICE]: Got Custom Login URI {0}, Grid does not provide default regions.", startLocation);
                                    return null;
                                }
                            }
                            return regions[0];
                        }
                        else
                        {
                            if (m_UserAgentService == null)
                            {
                                m_log.WarnFormat("[LLLOGIN SERVICE]: This llogin service is not running a user agent service, as such it can't lauch agents at foreign grids");
                                return null;
                            }
                            string[] parts = regionName.Split(new char[] { '@' });
                            if (parts.Length < 2)
                            {
                                m_log.InfoFormat("[LLLOGIN SERVICE]: Got Custom Login URI {0}, can't locate region {1}", startLocation, regionName);
                                return null;
                            }
                            // Valid specification of a remote grid

                            regionName = parts[0];
                            string domainLocator = parts[1];
                            parts = domainLocator.Split(new char[] { ':' });
                            string domainName = parts[0];
                            uint port = 0;
                            if (parts.Length > 1)
                                UInt32.TryParse(parts[1], out port);

                            GridRegion region = FindForeignRegion(domainName, port, regionName, out gatekeeper);
                            return region;
                        }
                    }
                    else
                    {
                        List<GridRegion> defaults = m_GridService.GetDefaultRegions(scopeID);
                        if (defaults != null && defaults.Count > 0)
                        {
                            where = "safe";
                            return defaults[0];
                        }
                        else
                            return null;
                    }
                }
                //response.LookAt = "[r0,r1,r0]";
                //// can be: last, home, safe, url
                //response.StartLocation = "url";

            }

        }

        private GridRegion FindForeignRegion(string domainName, uint port, string regionName, out GridRegion gatekeeper)
        {
            gatekeeper = new GridRegion();
            gatekeeper.ExternalHostName = domainName;
            gatekeeper.HttpPort = port;
            gatekeeper.RegionName = regionName;
            gatekeeper.InternalEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 0);

            UUID regionID;
            ulong handle;
            string imageURL = string.Empty, reason = string.Empty;
            if (m_GatekeeperConnector.LinkRegion(gatekeeper, out regionID, out handle, out domainName, out imageURL, out reason))
            {
                GridRegion destination = m_GatekeeperConnector.GetHyperlinkRegion(gatekeeper, regionID);
                return destination;
            }

            return null;
        }

        private string hostName = string.Empty;
        private int port = 0;

        private void SetHostAndPort(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                hostName = uri.Host;
                port = uri.Port;
            }
            catch
            {
                m_log.WarnFormat("[LLLogin SERVICE]: Unable to parse GatekeeperURL {0}", url);
            }
        }

        protected AgentCircuitData LaunchAgentAtGrid(GridRegion gatekeeper, GridRegion destination, UserAccount account, AvatarData avatar,
            UUID session, UUID secureSession, Vector3 position, string currentWhere, string viewer, string channel, string mac, string id0,
            IPEndPoint clientIP, out string where, out string reason, out GridRegion dest)
        {
            where = currentWhere;
            ISimulationService simConnector = null;
            reason = string.Empty;
            uint circuitCode = 0;
            AgentCircuitData aCircuit = null;

            if (m_UserAgentService == null)
            {
                // HG standalones have both a localSimulatonDll and a remoteSimulationDll
                // non-HG standalones have just a localSimulationDll
                // independent login servers have just a remoteSimulationDll
                if (m_LocalSimulationService != null)
                    simConnector = m_LocalSimulationService;
                else if (m_RemoteSimulationService != null)
                    simConnector = m_RemoteSimulationService;
            }
            else // User Agent Service is on
            {
                if (gatekeeper == null) // login to local grid
                {
                    if (hostName == string.Empty)
                        SetHostAndPort(m_GatekeeperURL);

                    gatekeeper = new GridRegion(destination);
                    gatekeeper.ExternalHostName = hostName;
                    gatekeeper.HttpPort = (uint)port;

                }
                else // login to foreign grid
                {
                }
            }

            bool success = false;

            if (m_UserAgentService == null && simConnector != null)
            {
                circuitCode = (uint)Util.RandomClass.Next(); ;
                aCircuit = MakeAgent(destination, account, avatar, session, secureSession, circuitCode, position, clientIP.Address.ToString(), viewer, channel, mac, id0);
                success = LaunchAgentDirectly(simConnector, destination, aCircuit, out reason);
                if (!success && m_GridService != null)
                {
					m_GridService.SetRegionUnsafe(destination.RegionID);
                    // Try the fallback regions
                    List<GridRegion> fallbacks = m_GridService.GetFallbackRegions(account.ScopeID, destination.RegionLocX, destination.RegionLocY);
                    if (fallbacks != null)
                    {
                        foreach (GridRegion r in fallbacks)
                        {
                            success = LaunchAgentDirectly(simConnector, r, aCircuit, out reason);
                            if (success)
                            {
                                aCircuit = MakeAgent(r, account, avatar, session, secureSession, circuitCode, position, viewer, clientIP.Address.ToString(), channel, mac, id0);
                                where = "safe";
                                destination = r;
                                break;
                            }
                            else
                                m_GridService.SetRegionUnsafe(r.RegionID);
                        }
                    }

                    //Try to find any safe region
                    List<GridRegion> safeRegions = m_GridService.GetSafeRegions(account.ScopeID, destination.RegionLocX, destination.RegionLocY);
                    if (safeRegions != null)
                    {
                        foreach (GridRegion r in safeRegions)
                        {
                            success = LaunchAgentDirectly(simConnector, r, aCircuit, out reason);
                            if (success)
                            {
                                aCircuit = MakeAgent(r, account, avatar, session, secureSession, circuitCode, position, viewer, channel, clientIP.Address.ToString(), mac, id0);
                                where = "safe";
                                destination = r;
                                break;
                            }
                            else
                                m_GridService.SetRegionUnsafe(r.RegionID);
                        }
                    }
                }
            }

            if (m_UserAgentService != null)
            {
                circuitCode = (uint)Util.RandomClass.Next(); ;
                aCircuit = MakeAgent(destination, account, avatar, session, secureSession, circuitCode, position, clientIP.Address.ToString(), viewer, channel, mac, id0);
                success = LaunchAgentIndirectly(gatekeeper, destination, aCircuit, clientIP, out reason);
                if (!success && m_GridService != null)
                {
                    m_GridService.SetRegionUnsafe(destination.RegionID);
                    // Try the fallback regions
                    List<GridRegion> fallbacks = m_GridService.GetFallbackRegions(account.ScopeID, destination.RegionLocX, destination.RegionLocY);
                    if (fallbacks != null)
                    {
                        foreach (GridRegion r in fallbacks)
                        {
                            success = LaunchAgentIndirectly(gatekeeper, r, aCircuit, clientIP, out reason);
                            if (success)
                            {
                                aCircuit = MakeAgent(r, account, avatar, session, secureSession, circuitCode, position, clientIP.Address.ToString(), viewer, channel, mac, id0);
                                where = "safe";
                                destination = r;
                                break;
                            }
                            else
                                m_GridService.SetRegionUnsafe(r.RegionID);
                        }
                    }

                    //Try to find any safe region
                    List<GridRegion> safeRegions = m_GridService.GetSafeRegions(account.ScopeID, destination.RegionLocX, destination.RegionLocY);
                    if (safeRegions != null)
                    {
                        foreach (GridRegion r in safeRegions)
                        {
                            success = LaunchAgentIndirectly(gatekeeper, r, aCircuit, clientIP, out reason);
                            if (success)
                            {
                                aCircuit = MakeAgent(r, account, avatar, session, secureSession, circuitCode, position, clientIP.Address.ToString(), viewer, channel, mac, id0);
                                where = "safe";
                                destination = r;
                                break;
                            }
                            else
                                m_GridService.SetRegionUnsafe(r.RegionID);
                        }
                    }
                }
            }
            dest = destination;
            if (success)
                return aCircuit;
            else
                return null;
        }

        private AgentCircuitData MakeAgent(GridRegion region, UserAccount account, 
            AvatarData avatar, UUID session, UUID secureSession, uint circuit, Vector3 position, 
            string ipaddress, string viewer, string channel, string mac, string id0)
        {
            AgentCircuitData aCircuit = new AgentCircuitData();

            aCircuit.AgentID = account.PrincipalID;
            if (avatar != null)
                aCircuit.Appearance = avatar.ToAvatarAppearance(account.PrincipalID);
            else
                aCircuit.Appearance = new AvatarAppearance(account.PrincipalID);

            //aCircuit.BaseFolder = irrelevant
            aCircuit.CapsPath = CapsUtil.GetRandomCapsObjectPath();
            aCircuit.child = false; // the first login agent is root
            aCircuit.ChildrenCapSeeds = new Dictionary<ulong, string>();
            aCircuit.circuitcode = circuit;
            aCircuit.firstname = account.FirstName;
            //aCircuit.InventoryFolder = irrelevant
            aCircuit.lastname = account.LastName;
            aCircuit.SecureSessionID = secureSession;
            aCircuit.SessionID = session;
            aCircuit.startpos = position;
            aCircuit.IPAddress = ipaddress;
            aCircuit.Viewer = viewer;
            aCircuit.Mac = mac;
            aCircuit.Id0 = id0;
            SetServiceURLs(aCircuit, account);

            return aCircuit;

            //m_UserAgentService.LoginAgentToGrid(aCircuit, GatekeeperServiceConnector, region, out reason);
            //if (simConnector.CreateAgent(region, aCircuit, 0, out reason))
            //    return aCircuit;

            //return null;

        }

        private void SetServiceURLs(AgentCircuitData aCircuit, UserAccount account)
        {
            aCircuit.ServiceURLs = new Dictionary<string, object>();
            if (account.ServiceURLs == null)
                return;

            foreach (KeyValuePair<string, object> kvp in account.ServiceURLs)
            {
                if (kvp.Value == null || (kvp.Value != null && kvp.Value.ToString() == string.Empty))
                {
                    aCircuit.ServiceURLs[kvp.Key] = m_LoginServerConfig.GetString(kvp.Key, string.Empty);
                }
                else
                {
                    aCircuit.ServiceURLs[kvp.Key] = kvp.Value;
                }
            }
        }

        private bool LaunchAgentDirectly(ISimulationService simConnector, GridRegion region, AgentCircuitData aCircuit, out string reason)
        {
            return simConnector.CreateAgent(region, aCircuit, (int)Constants.TeleportFlags.ViaLogin, out reason);
        }

        private bool LaunchAgentIndirectly(GridRegion gatekeeper, GridRegion destination, AgentCircuitData aCircuit, IPEndPoint clientIP, out string reason)
        {
            m_log.Debug("[LLOGIN SERVICE] Launching agent at " + destination.RegionName);
            if (m_UserAgentService.LoginAgentToGrid(aCircuit, gatekeeper, destination, clientIP, out reason))
                return true;
            return false;
        }

        #region Console Commands
        private void RegisterCommands()
        {
            //MainConsole.Instance.Commands.AddCommand
            MainConsole.Instance.Commands.AddCommand("loginservice", false, "login level",
                    "login level <level>",
                    "Set the minimum user level to log in", HandleLoginCommand);

            MainConsole.Instance.Commands.AddCommand("loginservice", false, "login reset",
                    "login reset",
                    "Reset the login level to allow all users",
                    HandleLoginCommand);

            MainConsole.Instance.Commands.AddCommand("loginservice", false, "login text",
                    "login text <text>",
                    "Set the text users will see on login", HandleLoginCommand);

        }

        private void HandleLoginCommand(string module, string[] cmd)
        {
            string subcommand = cmd[1];

            switch (subcommand)
            {
                case "level":
                    // Set the minimum level to allow login 
                    // Useful to allow grid update without worrying about users.
                    // or fixing critical issues
                    //
                    if (cmd.Length > 2)
                        Int32.TryParse(cmd[2], out m_MinLoginLevel);
                    break;
                case "reset":
                    m_MinLoginLevel = 0;
                    break;
                case "text":
                    if (cmd.Length > 2)
                        m_WelcomeMessage = cmd[2];
                    break;
            }
        }
    }

        #endregion

    public class GridAvatarArchiver : IAvatarAppearanceArchiver
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private IUserAccountService UserAccountService;
        private IAvatarService AvatarService;
        private IInventoryService InventoryService;
        public GridAvatarArchiver(IUserAccountService ACS, IAvatarService AS, IInventoryService IS)
        {
            UserAccountService = ACS;
            AvatarService = AS;
            InventoryService = IS;
            MainConsole.Instance.Commands.AddCommand("region", false, "save avatar archive", "save avatar archive <First> <Last> <Filename> <FolderNameToSaveInto>", "Saves appearance to an avatar archive archive (Note: put \"\" around the FolderName if you need more than one word)", HandleSaveAvatarArchive);
            MainConsole.Instance.Commands.AddCommand("region", false, "load avatar archive", "load avatar archive <First> <Last> <Filename>", "Loads appearance from an avatar archive archive", HandleLoadAvatarArchive);
        }
        #region Console Commands

        protected void HandleLoadAvatarArchive(string module, string[] cmdparams)
        {
            if (cmdparams.Length != 6)
            {
                m_log.Debug("[AvatarArchive] Not enough parameters!");
                return;
            }
            UserAccount account = UserAccountService.GetUserAccount(UUID.Zero, cmdparams[3], cmdparams[4]);
            LoadAvatarArchive(cmdparams[5], cmdparams[3], cmdparams[4]);
        }

        protected void HandleSaveAvatarArchive(string module, string[] cmdparams)
        {
            if (cmdparams.Length != 7)
            {
                m_log.Debug("[AvatarArchive] Not enough parameters!");
                return;
            }
            UserAccount account = UserAccountService.GetUserAccount(UUID.Zero, cmdparams[3], cmdparams[4]);
            if (account == null)
            {
                m_log.Error("[AvatarArchive] User not found!");
                return;
            }
            AvatarData avatarData = AvatarService.GetAvatar(account.PrincipalID);
            AvatarAppearance appearance = avatarData.ToAvatarAppearance(account.PrincipalID);
            if (cmdparams[5].EndsWith(".database"))
            {
                string ArchiveName = cmdparams[5].Substring(0, cmdparams[5].Length - 9);
                string ArchiveXML = MakeXMLFormat(appearance, cmdparams[6]);
                
                AvatarArchive archive = new AvatarArchive();
                archive.ArchiveXML = ArchiveXML;
                archive.Name = ArchiveName;

                DataManager.RequestPlugin<IAvatarArchiverConnector>().SaveAvatarArchive(archive);

                m_log.Debug("[AvatarArchive] Saved archive to database " + cmdparams[5]);
            }
            else
            {
                StreamWriter writer = new StreamWriter(cmdparams[5]);
                writer.Write(MakeXMLFormat(appearance, cmdparams[6]));
                writer.Close();
                writer.Dispose();
                m_log.Debug("[AvatarArchive] Saved archive to " + cmdparams[5]);
            }
        }

        private string MakeXMLFormat(AvatarAppearance appearance, string FolderNameToSaveInto)
        {
            string ArchiveXML = "";

            ArchiveXML += "<avatar>\n";
            ArchiveXML += "<" + appearance.AvatarHeight + ">\n";
            ArchiveXML += "<" + appearance.BodyAsset + ">\n";
            ArchiveXML += "<" + appearance.BodyItem + ">\n";
            ArchiveXML += "<" + appearance.EyesAsset + ">\n";
            ArchiveXML += "<" + appearance.EyesItem + ">\n";
            ArchiveXML += "<" + appearance.GlovesAsset + ">\n";
            ArchiveXML += "<" + appearance.GlovesItem + ">\n";
            ArchiveXML += "<" + appearance.HairAsset + ">\n";
            ArchiveXML += "<" + appearance.HairItem + ">\n";
            ArchiveXML += "<" + appearance.HipOffset + ">\n";
            ArchiveXML += "<" + appearance.JacketAsset + ">\n";
            ArchiveXML += "<" + appearance.JacketItem + ">\n";
            ArchiveXML += "<" + appearance.Owner + ">\n";
            ArchiveXML += "<" + appearance.PantsAsset + ">\n";
            ArchiveXML += "<" + appearance.PantsItem + ">\n";
            ArchiveXML += "<" + appearance.Serial + ">\n";
            ArchiveXML += "<" + appearance.ShirtAsset + ">\n";
            ArchiveXML += "<" + appearance.ShirtItem + ">\n";
            ArchiveXML += "<" + appearance.ShoesAsset + ">\n";
            ArchiveXML += "<" + appearance.ShoesItem + ">\n";
            ArchiveXML += "<" + appearance.SkinAsset + ">\n";
            ArchiveXML += "<" + appearance.SkinItem + ">\n";
            ArchiveXML += "<" + appearance.SkirtAsset + ">\n";
            ArchiveXML += "<" + appearance.SkirtItem + ">\n";
            ArchiveXML += "<" + appearance.SocksAsset + ">\n";
            ArchiveXML += "<" + appearance.SocksItem + ">\n";
            ArchiveXML += "<" + appearance.UnderPantsAsset + ">\n";
            ArchiveXML += "<" + appearance.UnderPantsItem + ">\n";
            ArchiveXML += "<" + appearance.UnderShirtAsset + ">\n";
            ArchiveXML += "<" + appearance.UnderShirtItem + ">\n";
            ArchiveXML += "<" + FolderNameToSaveInto + ">\n";
            ArchiveXML += "<VisualParams>\n";
            foreach (Byte Byte in appearance.VisualParams)
            {
                ArchiveXML += "</VP" + Convert.ToString(Byte) + ">\n";
            }
            ArchiveXML += "</VisualParams>\n";
            ArchiveXML += "<wearables>\n";
            foreach (AvatarWearable wear in appearance.Wearables)
            {
                ArchiveXML += "<WA" + wear.AssetID + ">\n";
                ArchiveXML += "<WI" + wear.ItemID + ">\n";
            }
            ArchiveXML += "</wearables>\n";
            ArchiveXML += "<TEXTURE" + appearance.Texture.ToString().Replace("\n", "") + "TEXTURE>\n";
            ArchiveXML += "</avatar>";
            Hashtable attachments = appearance.GetAttachments();
            ArchiveXML += "<attachments>\n";
            if (attachments != null)
            {
                foreach (DictionaryEntry element in attachments)
                {
                    Hashtable attachInfo = (Hashtable)element.Value;

                    InventoryItemBase IB = new InventoryItemBase(UUID.Parse(attachInfo["item"].ToString()));
                    IB = InventoryService.GetItem(IB);
                    ArchiveXML += "<AI" + attachInfo["item"] + ">\n";
                    ArchiveXML += "<AA" + IB.AssetID + ">\n";
                    ArchiveXML += "<AP" + (int)element.Key + ">\n";
                }
            }
            ArchiveXML += "</attachments>";
            return ArchiveXML;
        }

        #endregion

        public void LoadAvatarArchive(string FileName, string First, string Last)
        {
            List<string> file = new List<string>();
            UserAccount account = UserAccountService.GetUserAccount(UUID.Zero, First, Last);
            if (account == null)
            {
                m_log.Error("[AvatarArchive] User not found!");
                return;
            }

            string FolderNameToLoadInto = "";
            
            if (FileName.EndsWith(".database"))
            {
                m_log.Debug("[AvatarArchive] Loading archive from the database " + FileName);
                
                FileName = FileName.Substring(0,FileName.Length-9);

                Aurora.Framework.IAvatarArchiverConnector avarchiver = DataManager.RequestPlugin<IAvatarArchiverConnector>();
                AvatarArchive archive = avarchiver.GetAvatarArchive(FileName);

                string[] lines = archive.ArchiveXML.Split('\n');
                file = new List<string>(lines);
            }
            else
            {
                m_log.Debug("[AvatarArchive] Loading archive from " + FileName);
                StreamReader reader = new StreamReader(FileName);
                string line = reader.ReadToEnd();
                string[] lines = line.Split('\n');
                file = new List<string>(lines);
                reader.Close();
                reader.Dispose();
            }

            List<UUID> AttachmentUUIDs = new List<UUID>();
            List<string> AttachmentPoints = new List<string>();
            List<string> AttachmentAssets = new List<string>();
            AvatarAppearance appearance = ConvertXMLToAvatarAppearance(file, out AttachmentUUIDs, out AttachmentPoints, out AttachmentAssets, out FolderNameToLoadInto);
            appearance.Owner = account.PrincipalID;
            List<InventoryItemBase> items = new List<InventoryItemBase>();

            InventoryFolderBase AppearanceFolder = InventoryService.GetFolderForType(account.PrincipalID, AssetType.Clothing);

            UUID newFolderId = UUID.Random();

            InventoryFolderBase folderForAppearance
                = new InventoryFolderBase(
                    newFolderId, FolderNameToLoadInto, account.PrincipalID,
                    -1, AppearanceFolder.ID, 1);

            InventoryService.AddFolder(folderForAppearance);
            folderForAppearance = InventoryService.GetFolder(folderForAppearance);

            #region Appearance setup

            if (appearance.BodyItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.BodyItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.BodyItem, folderForAppearance);
                items.Add(IB);
                appearance.BodyItem = IB.ID;
            }

            if (appearance.EyesItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.EyesItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.EyesItem, folderForAppearance);
                items.Add(IB);
                appearance.EyesItem = IB.ID;
            }

            if (appearance.GlovesItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.GlovesItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.GlovesItem, folderForAppearance);
                items.Add(IB);
                appearance.GlovesItem = IB.ID;
            }

            if (appearance.HairItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.HairItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.HairItem, folderForAppearance);
                items.Add(IB);
                appearance.HairItem = IB.ID;
            }

            if (appearance.JacketItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.JacketItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.JacketItem, folderForAppearance);
                items.Add(IB);
                appearance.JacketItem = IB.ID;
            }

            if (appearance.PantsItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.PantsItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.PantsItem, folderForAppearance);
                items.Add(IB);
                appearance.PantsItem = IB.ID;
            }

            if (appearance.ShirtItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.ShirtItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.ShirtItem, folderForAppearance);
                items.Add(IB);
                appearance.ShirtItem = IB.ID;
            }

            if (appearance.ShoesItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.ShoesItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.ShoesItem, folderForAppearance);
                items.Add(IB);
                appearance.ShoesItem = IB.ID;
            }

            if (appearance.SkinItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.SkinItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.SkinItem, folderForAppearance);
                items.Add(IB);
                appearance.SkinItem = IB.ID;
            }

            if (appearance.SkirtItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.SkirtItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.SkirtItem, folderForAppearance);
                items.Add(IB);
                appearance.SkirtItem = IB.ID;
            }

            if (appearance.SocksItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.SocksItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.SocksItem, folderForAppearance);
                items.Add(IB);
                appearance.SocksItem = IB.ID;
            }

            if (appearance.UnderPantsItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.UnderPantsItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.UnderPantsItem, folderForAppearance);
                items.Add(IB);
                appearance.UnderPantsItem = IB.ID;
            }

            if (appearance.UnderShirtItem != UUID.Zero)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(appearance.UnderShirtItem));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, appearance.UnderShirtItem, folderForAppearance);
                items.Add(IB);
                appearance.UnderShirtItem = IB.ID;
            }

            appearance.ClearAttachments(); //Clear so that we can rebuild
            int i = 0;
            foreach (UUID uuid in AttachmentUUIDs)
            {
                InventoryItemBase IB = InventoryService.GetItem(new InventoryItemBase(uuid));
                IB = GiveInventoryItem(IB.CreatorIdAsUuid, appearance.Owner, uuid, folderForAppearance);
                items.Add(IB);
                appearance.SetAttachment(int.Parse(AttachmentPoints[i]), IB.ID, UUID.Parse(AttachmentAssets[i]));
                i++;
            }

            #endregion

            appearance.Owner = account.PrincipalID;
            AvatarData adata = new AvatarData(appearance);
            AvatarService.SetAvatar(account.PrincipalID, adata);
            m_log.Debug("[AvatarArchive] Loaded archive from " + FileName);
        }

        private AvatarAppearance ConvertXMLToAvatarAppearance(List<string> file, out List<UUID> AttachmentIDs, out List<string> AttachmentPoints, out List<string> AttachmentAsset, out string FolderNameToPlaceAppearanceIn)
        {
            AvatarAppearance appearance = new AvatarAppearance();
            List<string> newFile = new List<string>();
            foreach (string line in file)
            {
                string newLine = line.TrimStart('<');
                newFile.Add(newLine.TrimEnd('>'));
            }
            appearance.AvatarHeight = Convert.ToInt32(newFile[1]);
            appearance.BodyAsset = new UUID(newFile[2]);
            appearance.BodyItem = new UUID(newFile[3]);
            appearance.EyesAsset = new UUID(newFile[4]);
            appearance.EyesItem = new UUID(newFile[5]);
            appearance.GlovesAsset = new UUID(newFile[6]);
            appearance.GlovesItem = new UUID(newFile[7]);
            appearance.HairAsset = new UUID(newFile[8]);
            appearance.HairItem = new UUID(newFile[9]);
            //Skip Hip Offset
            appearance.JacketAsset = new UUID(newFile[11]);
            appearance.JacketItem = new UUID(newFile[12]);
            appearance.Owner = new UUID(newFile[13]);
            appearance.PantsAsset = new UUID(newFile[14]);
            appearance.PantsItem = new UUID(newFile[15]);
            appearance.Serial = Convert.ToInt32(newFile[16]);
            appearance.ShirtAsset = new UUID(newFile[17]);
            appearance.ShirtItem = new UUID(newFile[18]);
            appearance.ShoesAsset = new UUID(newFile[19]);
            appearance.ShoesItem = new UUID(newFile[20]);
            appearance.SkinAsset = new UUID(newFile[21]);
            appearance.SkinItem = new UUID(newFile[22]);
            appearance.SkirtAsset = new UUID(newFile[23]);
            appearance.SkirtItem = new UUID(newFile[24]);
            appearance.SocksAsset = new UUID(newFile[25]);
            appearance.SocksItem = new UUID(newFile[26]);
            //appearance.Texture = new Primitive.TextureEntry(newFile[27],);
            appearance.UnderPantsAsset = new UUID(newFile[27]);
            appearance.UnderPantsItem = new UUID(newFile[28]);
            appearance.UnderShirtAsset = new UUID(newFile[29]);
            appearance.UnderShirtItem = new UUID(newFile[30]);
            FolderNameToPlaceAppearanceIn = newFile[31];

            Byte[] bytes = new byte[218];
            int i = 0;
            while (i <= 31)
            {
                newFile.RemoveAt(0); //Clear out the already processed parts
                i++;
            }
            i = 0;
            if (newFile[0] == "VisualParams")
            {
                foreach (string partLine in newFile)
                {
                    if (partLine.StartsWith("/VP"))
                    {
                        string newpartLine = "";
                        newpartLine = partLine.Replace("/VP", "");
                        bytes[i] = Convert.ToByte(newpartLine);
                        i++;
                    }
                }
            }
            appearance.VisualParams = bytes;
            List<string> WearableAsset = new List<string>();
            List<string> WearableItem = new List<string>();
            string texture = "";
            AttachmentIDs = new List<UUID>();
            AttachmentPoints = new List<string>();
            AttachmentAsset = new List<string>();
            foreach (string partLine in newFile)
            {
                if (partLine.StartsWith("WA"))
                {
                    string newpartLine = "";
                    newpartLine = partLine.Replace("WA", "");
                    WearableAsset.Add(newpartLine);
                }
                if (partLine.StartsWith("WI"))
                {
                    string newpartLine = "";
                    newpartLine = partLine.Replace("WI", "");
                    WearableItem.Add(newpartLine);
                }
                if (partLine.StartsWith("TEXTURE"))
                {
                    string newpartLine = "";
                    newpartLine = partLine.Replace("TEXTURE", "");
                    texture = newpartLine;
                }
                if (partLine.StartsWith("AI"))
                {
                    string newpartLine = "";
                    newpartLine = partLine.Replace("AI", "");
                    AttachmentIDs.Add(new UUID(newpartLine));
                }
                if (partLine.StartsWith("AA"))
                {
                    string newpartLine = "";
                    newpartLine = partLine.Replace("AA", "");
                    AttachmentAsset.Add(newpartLine);
                }
                if (partLine.StartsWith("AP"))
                {
                    string newpartLine = "";
                    newpartLine = partLine.Replace("AP", "");
                    AttachmentPoints.Add(newpartLine);
                }
            }
            //byte[] textureBytes = Utils.StringToBytes(texture);
            //appearance.Texture = new Primitive.TextureEntry(textureBytes, 0, textureBytes.Length);
            AvatarWearable[] wearables = new AvatarWearable[15];
            i = 0;
            foreach (string asset in WearableAsset)
            {
                AvatarWearable wearable = new AvatarWearable(new UUID(asset), new UUID(WearableItem[i]));
                wearables[i] = wearable;
                i++;
            }
            i = 0;
            foreach (string asset in AttachmentAsset)
            {
                appearance.SetAttachment(Convert.ToInt32(AttachmentPoints[i]), AttachmentIDs[i], new UUID(asset));
                i++;
            }
            return appearance;
        }

        private InventoryItemBase GiveInventoryItem(UUID senderId, UUID recipient, UUID itemId, InventoryFolderBase parentFolder)
        {
            InventoryItemBase item = new InventoryItemBase(itemId, senderId);
            item = InventoryService.GetItem(item);


            InventoryItemBase itemCopy = new InventoryItemBase();
            itemCopy.Owner = recipient;
            itemCopy.CreatorId = item.CreatorId;
            itemCopy.ID = UUID.Random();
            itemCopy.AssetID = item.AssetID;
            itemCopy.Description = item.Description;
            itemCopy.Name = item.Name;
            itemCopy.AssetType = item.AssetType;
            itemCopy.InvType = item.InvType;
            itemCopy.Folder = UUID.Zero;

            //Give full permissions for them
            itemCopy.NextPermissions = (uint)PermissionMask.All;
            itemCopy.GroupPermissions = (uint)PermissionMask.All;
            itemCopy.EveryOnePermissions = (uint)PermissionMask.All;
            itemCopy.CurrentPermissions = (uint)PermissionMask.All;

            if (parentFolder == null)
            {
                InventoryFolderBase folder = InventoryService.GetFolderForType(recipient, (AssetType)itemCopy.AssetType);

                if (folder != null)
                    itemCopy.Folder = folder.ID;
                else
                {
                    InventoryFolderBase root = InventoryService.GetRootFolder(recipient);

                    if (root != null)
                        itemCopy.Folder = root.ID;
                    else
                        return null; // No destination
                }
            }
            else
                itemCopy.Folder = parentFolder.ID; //We already have a folder to put it in

            itemCopy.GroupID = UUID.Zero;
            itemCopy.GroupOwned = false;
            itemCopy.Flags = item.Flags;
            itemCopy.SalePrice = item.SalePrice;
            itemCopy.SaleType = item.SaleType;

            InventoryService.AddItem(itemCopy);

            return itemCopy;
        }
    }

    public class GridAvatarProfileArchiver
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private IUserAccountService UserAccountService;
        public GridAvatarProfileArchiver(IUserAccountService UAS)
        {
            UserAccountService = UAS;
            MainConsole.Instance.Commands.AddCommand("region", false, "save avatar profile",
                                          "save avatar profile <First> <Last> <Filename>",
                                          "Saves profile and avatar data to an archive", HandleSaveAvatarProfile);
            MainConsole.Instance.Commands.AddCommand("region", false, "load avatar profile",
                                          "load avatar profile <First> <Last> <Filename>",
                                          "Loads profile and avatar data from an archive", HandleLoadAvatarProfile);
        }

        protected void HandleLoadAvatarProfile(string module, string[] cmdparams)
        {
            if (cmdparams.Length != 6)
            {
                m_log.Debug("[AvatarProfileArchiver] Not enough parameters!");
                return;
            }
            StreamReader reader = new StreamReader(cmdparams[5]);

            string document = reader.ReadToEnd();
            string[] lines = document.Split('\n');
            List<string> file = new List<string>(lines);
            Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(file[1]);
            
            Dictionary<string, object> results = replyData["result"] as Dictionary<string, object>;
            UserAccount UDA = new UserAccount();
            UDA.FirstName = cmdparams[3];
            UDA.LastName = cmdparams[4];
            UDA.PrincipalID = UUID.Random();
            UDA.ScopeID = UUID.Zero;
            UDA.UserFlags = int.Parse(results["UserFlags"].ToString());
            UDA.UserLevel = 0; //For security... Don't want everyone loading full god mode.
            UDA.UserTitle = results["UserTitle"].ToString();
            UDA.Email = results["Email"].ToString();
            UDA.Created = int.Parse(results["Created"].ToString());
            if (results.ContainsKey("ServiceURLs") && results["ServiceURLs"] != null)
            {
                UDA.ServiceURLs = new Dictionary<string, object>();
                string str = results["ServiceURLs"].ToString();
                if (str != string.Empty)
                {
                    string[] parts = str.Split(new char[] { ';' });
                    Dictionary<string, object> dic = new Dictionary<string, object>();
                    foreach (string s in parts)
                    {
                        string[] parts2 = s.Split(new char[] { '*' });
                        if (parts2.Length == 2)
                            UDA.ServiceURLs[parts2[0]] = parts2[1];
                    }
                }
            }
            UserAccountService.StoreUserAccount(UDA);

            
            replyData = ServerUtils.ParseXmlResponse(file[2]);
            IUserProfileInfo UPI = new IUserProfileInfo();
            UPI.FromKVP(replyData["result"] as Dictionary<string, object>);
            //Update the principle ID to the new user.
            UPI.PrincipalID = UDA.PrincipalID;

            IProfileConnector profileData = DataManager.RequestPlugin<IProfileConnector>();
            if (profileData.GetUserProfile(UPI.PrincipalID) == null)
                profileData.CreateNewProfile(UPI.PrincipalID);

            profileData.UpdateUserProfile(UPI);

            
            reader.Close();
            reader.Dispose();

            m_log.Debug("[AvatarProfileArchiver] Loaded Avatar Profile from " + cmdparams[5]);
        }
        protected void HandleSaveAvatarProfile(string module, string[] cmdparams)
        {
            if (cmdparams.Length != 6)
            {
                m_log.Debug("[AvatarProfileArchiver] Not enough parameters!");
                return;
            }
            UserAccount account = UserAccountService.GetUserAccount(UUID.Zero, cmdparams[3], cmdparams[4]);
            IProfileConnector data = DataManager.RequestPlugin<IProfileConnector>();
            IUserProfileInfo profile = data.GetUserProfile(account.PrincipalID);

            Dictionary<string, object> result = new Dictionary<string, object>();
            result["result"] = profile.ToKeyValuePairs();
            string UPIxmlString = ServerUtils.BuildXmlResponse(result);

            result["result"] = account.ToKeyValuePairs();
            string UDAxmlString = ServerUtils.BuildXmlResponse(result);

            StreamWriter writer = new StreamWriter(cmdparams[5]);
            writer.Write("<profile>\n");
            writer.Write(UDAxmlString + "\n");
            writer.Write(UPIxmlString + "\n");
            writer.Write("</profile>\n");
            m_log.Debug("[AvatarProfileArchiver] Saved Avatar Profile to " + cmdparams[5]);
            writer.Close();
            writer.Dispose();
        }
    }
}