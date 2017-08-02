﻿/*

___    ___  ______   ________    __       ______
\  \  /  / |   ___| |__    __|  /  \     |   ___|
 \  \/  /  |  |___     |  |    / /\ \    |  |__
  |    |   |   ___|    |  |   /  __  \    \__  \
 /	/\  \  |  |___     |  |  /  /  \  \   ___|  |
/__/  \__\ |______|    |__| /__/    \__\ |______|

Written by Paul "Xetas" Abramov


*/

using System;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using SteamKit2;

namespace CTB.Web
{
    public class SteamWeb
    {
        #region Singleton
        public static SteamWeb Instance { get; private set; }
        #endregion

        public string SessionID { get; private set; }
        public string SteamLogin { get; private set; }
        public string SteamLoginSecure { get; private set; }
        public string APIKey { get; private set; }

        private SteamClient m_steamClient;

        private readonly SteamUser m_steamUser;
        public readonly string m_SteamCommunityHost = "steamcommunity.com";
        public readonly string m_SteamStoreHost = "store.steampowered.com";
        public readonly string m_APISteamAddress = "http://api.steampowered.com/{0}/{1}/{2}";

        /// <summary>
        /// Initialize the SteamWeb object with the apikey
        /// </summary>
        /// <param name="_steamUser"></param>
        private SteamWeb(SteamUser _steamUser)
        {
            m_steamUser = _steamUser;
        }

        /// <summary>
        /// Set the instance of the Singleton
        /// </summary>
        /// <param name="_steamUser"></param>
        public static void SetInstance(SteamUser _steamUser)
        {
            if (Instance == null)
            {
                Instance = new SteamWeb(_steamUser);
            }
        }

        /// <summary>
        /// Send a request to the homepage of steam, check if there is something with "profiles/OurSteamID64/friends"
        /// If there is such a content inside the string, then we are still authenticated return true, so we know we are loggedon
        /// If there is not such a content inside the string, then we are not authenticated and try to authenticate to the web again
        /// To authenticate we have to request a new nonce, which will be needed to authenticate
        /// </summary>
        /// <returns></returns>
        public async Task<bool> RefreshSessionIfNeeded()
        {
            string response = await WebHelper.Instance.GetStringFromRequest("http://steamcommunity.com/my/").ConfigureAwait(false);

            bool isNotLoggedOn = response.Contains("Sign In");

            if (isNotLoggedOn)
            {
                SteamUser.WebAPIUserNonceCallback userNonceCallback;

                try
                {
                    userNonceCallback = await m_steamUser.RequestWebAPIUserNonce();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    return false;
                }

                if (string.IsNullOrEmpty(userNonceCallback?.Nonce))
                {
                    Console.WriteLine("Usernonce is empty");
                }

                Console.WriteLine("Reauthenticating...");

                return AuthenticateUser(m_steamClient, userNonceCallback?.Nonce);
            }
            else
            {
                return true;
            }
        }

        /// <summary>
        /// Authenticate the user at the online services of Steam
        /// </summary>
        public bool AuthenticateUser(SteamClient _steamClient, string _webAPIUserNonce )
        {
            m_steamClient = _steamClient;

            // Get the interface for the authentication of the steamuser
            using (dynamic authenticator = WebAPI.GetInterface("ISteamUserAuth"))
            {
                SessionID = Convert.ToBase64String(Encoding.UTF8.GetBytes(_steamClient.SteamID.ToString()));

                // Generate a random block of 32 bytes for the security
                byte[] sessionKey = CryptoHelper.GenerateRandomBlock(32);

                // Encrypt the above generated block of bytes with the Steam systems public key
                byte[] encryptedSessionKey;
                using (RSACrypto rsa = new RSACrypto(KeyDictionary.GetPublicKey(_steamClient.ConnectedUniverse)))
                {
                    encryptedSessionKey = rsa.Encrypt(sessionKey);
                }

                // Copy the string into the bytearray
                byte[] loginkey = new byte[_webAPIUserNonce.Length];
                Array.Copy(Encoding.ASCII.GetBytes(_webAPIUserNonce), loginkey, _webAPIUserNonce.Length);

                // AES encrypt the loginkey with our sessionkey
                byte[] encryptedLoginKey = CryptoHelper.SymmetricEncrypt(loginkey, sessionKey);

                // The value returned by the AuthenticateUser function are KeyValues
                KeyValue authResult;

                // Always TRY to work with interfaces, because it could go wrong and destroy everything
                try
                {
                    authResult = authenticator.AuthenticateUser(
                        steamid: _steamClient.SteamID.ConvertToUInt64(),
                        sessionkey: Encoding.ASCII.GetString(WebUtility.UrlEncodeToBytes(encryptedSessionKey, 0, encryptedSessionKey.Length)),
                        encrypted_loginkey: Encoding.ASCII.GetString(WebUtility.UrlEncodeToBytes(encryptedLoginKey, 0, encryptedLoginKey.Length)),
                        method: WebRequestMethods.Http.Post,
                        secure: true);
                }
                catch (Exception e)
                {
                    if(!e.Message.Contains("403"))
                    {
                        Console.WriteLine(e);
                    }
                    return false;
                }

                // Double check if it is null then return because we do not have anything to do here
                if (authResult == null)
                {
                    return false;
                }

                // Set the cookies
                SteamLogin = authResult["token"].Value;
                SteamLoginSecure = authResult["tokensecure"].Value;

                // Create a new instance of the cookieContainer
                // After loosing connection a second m_domainTable will be created, holding the sessionID from the time we were not authenticated
                // This will lead to inactive session while requesting API/WebCalls
                WebHelper.Instance.m_CookieContainer = new CookieContainer();

                WebHelper.Instance.m_CookieContainer.Add(new Cookie("sessionid", SessionID, string.Empty, m_SteamStoreHost));
                WebHelper.Instance.m_CookieContainer.Add(new Cookie("sessionid", SessionID, string.Empty, m_SteamCommunityHost));

                WebHelper.Instance.m_CookieContainer.Add(new Cookie("steamLogin", SteamLogin, string.Empty, m_SteamStoreHost));
                WebHelper.Instance.m_CookieContainer.Add(new Cookie("steamLogin", SteamLogin, string.Empty, m_SteamCommunityHost));

                WebHelper.Instance.m_CookieContainer.Add(new Cookie("steamLoginSecure", SteamLoginSecure, string.Empty, m_SteamStoreHost));
                WebHelper.Instance.m_CookieContainer.Add(new Cookie("steamLoginSecure", SteamLoginSecure, string.Empty, m_SteamCommunityHost));

                return true;
            }
        }

        /// <summary>
        /// Make a call to the dev/apikey url to get the apikey for this account
        /// Set the Apikey from the response we got
        /// </summary>
        public async Task RequestAPiKey()
        {
            string url = $"https://{m_SteamCommunityHost}/dev/apikey?l=english";

            string response = await WebHelper.Instance.GetStringFromRequest(url).ConfigureAwait(false);

            await SetApiKey(response);
        }

        /// <summary>
        /// Make a call to the dev/registerkey url to register an apikey for this account
        /// Give the call some data, which will be needed to register a key
        /// Post the data and receive an answer and set the ApiKey from this answer
        /// </summary>
        private async Task RegisterApiKey()
        {
            string url = $"https://{m_SteamCommunityHost}/dev/registerkey";

            NameValueCollection data = new NameValueCollection()
            {
                {"domain", "localhost"},
                {"agreeToTerms", "agreed"},
                {"sessionid", SessionID},
                {"Submit", "Register"}
            };

            string response = await WebHelper.Instance.GetStringFromRequest(url, data, false).ConfigureAwait(false);

            await SetApiKey(response);
        }

        /// <summary>
        /// response should be like:
        /// Key: XXXXXXXXXXXXXXXXXXXX
        /// we want to cut off the "Key: " part, so split at the whitespace and take the right part, second part in the array we get
        /// If there is no match, we have to register an apikey first
        /// </summary>
        /// <param name="_response"></param>
        private async Task SetApiKey(string _response)
        {
            HtmlDocument document = new HtmlDocument();
            document.LoadHtml(_response);

            // XPath with HTMLAgility (easier way and less error-prone)
            HtmlNode node = document.DocumentNode.SelectNodes("//div[@id='bodyContents_ex']/p")[0];

            if (node != null)
            {
                APIKey = node.InnerHtml.Split(' ')[1];
            }
            else
            {
                await RegisterApiKey().ConfigureAwait(false);
            }

            // HTMLAgility without XPATH
            // var div = document.GetElementbyId("bodyContents_ex").Elements("p");

            
            // Get the APIKey with regex instead of HTMLAgility and XPATH

            //Match keyMatch = Regex.Match(_response, @"Key(?:[\s:]+)([\w]+[\d]+)", RegexOptions.IgnoreCase);
            //
            //if (keyMatch.Success)
            //{
            //    APIKey = keyMatch.Value.Split(' ')[1];
            //}
            //else
            //{
            //    RegisterApiKey();
            //}
        }
    }
}