﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Protocols.WSTrust;
using System.IdentityModel.Tokens;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Owin.Security.OpenIdConnect;
using Microsoft.Owin.Security.OAuth;
using Microsoft.Owin.Security.OpenIdConnect.Server;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin.Security;
using System.Globalization;

namespace Owin
{
    public static class OpenIdConnectServerExtensions
    {
        /// <summary>
        /// Adds Open Id Connect Authorization Server capabilities to an OWIN web application. This middleware
        /// performs the request processing for the Authorize and Token endpoints defined by the OpenId Connect specification.
        /// </summary>
        /// <param name="app">The web application builder</param>
        /// <param name="options">Options which control the behavior of the Authorization Server.</param>
        /// <returns>The application builder</returns>
        public static IAppBuilder UseOpenIdConnectAuthorizationServer(this IAppBuilder app, OpenIdConnectServerOptions options)
        {
            if (app == null)
            {
                throw new ArgumentNullException("app");
            }

            var currentProvider = options.Provider;
            if (currentProvider == null) throw new ArgumentNullException("options.Provider");
            if (options.ServerClaimsMapper == null) throw new ArgumentNullException("options.ServerClaimsMapper");
            if (options.SigningCredentials == null) throw new ArgumentNullException("options.SigningCredentials");
            if (string.IsNullOrWhiteSpace(options.IssuerName)) throw new ArgumentException("options.IssuerName");

            var newProvider = WrapProvider(currentProvider);
            options.Provider = newProvider;

            var originalOnAuthorizationEndpointResponse = newProvider.OnAuthorizationEndpointResponse;

            newProvider.OnAuthorizationEndpointResponse = async (ctx) =>
            {
                if (originalOnAuthorizationEndpointResponse != null)
                {
                    await originalOnAuthorizationEndpointResponse(ctx);
                }

                if (!ctx.AuthorizeEndpointRequest.ContainsGrantType(OpenIdConnectResponseTypes.IdToken)) return;
                if (!ctx.AuthorizeEndpointRequest.Scope.Contains(OpenIdConnectScopes.OpenId)) return;
                var idToken = CreateIdToken(options, ctx.Identity, ctx.Properties, ctx.AuthorizeEndpointRequest.ClientId, ctx.AccessToken, ctx.AuthorizationCode, ctx.Request.Query["nonce"]);
                ctx.AdditionalResponseParameters.Add(OpenIdConnectResponseTypes.IdToken, idToken);
            };
            
            var originalOnTokenEndpointResponse = newProvider.OnTokenEndpointResponse;

            newProvider.OnTokenEndpointResponse = async (ctx) =>
            {
                if (originalOnTokenEndpointResponse != null)
                {
                    await originalOnTokenEndpointResponse(ctx);
                }

                var idToken = CreateIdToken(options, ctx.Identity, ctx.Properties, ctx.TokenEndpointRequest.ClientId, ctx.AccessToken);
                ctx.AdditionalResponseParameters.Add(OpenIdConnectResponseTypes.IdToken, idToken);
            };

            app.Use(typeof(OAuthAuthorizationServerMiddleware), app, options);
            return app;
        }

        private static OAuthAuthorizationServerProvider WrapProvider(IOAuthAuthorizationServerProvider provider)
        {
            return new OAuthAuthorizationServerProvider
            {
                OnAuthorizationEndpointResponse = context => provider.AuthorizationEndpointResponse(context),
                OnAuthorizeEndpoint = context => provider.AuthorizeEndpoint(context),
                OnGrantAuthorizationCode = context => provider.GrantAuthorizationCode(context),
                OnGrantClientCredentials = context => provider.GrantClientCredentials(context),
                OnGrantCustomExtension = context => provider.GrantCustomExtension(context),
                OnGrantRefreshToken = context => provider.GrantRefreshToken(context),
                OnGrantResourceOwnerCredentials = context => provider.GrantResourceOwnerCredentials(context),
                OnMatchEndpoint = context => provider.MatchEndpoint(context),
                OnTokenEndpoint = context => provider.TokenEndpoint(context),
                OnTokenEndpointResponse = context => provider.TokenEndpointResponse(context),
                OnValidateAuthorizeRequest = context => provider.ValidateAuthorizeRequest(context),
                OnValidateClientAuthentication = context => provider.ValidateClientAuthentication(context),
                OnValidateClientRedirectUri = context => provider.ValidateClientRedirectUri(context),
                OnValidateTokenRequest = context => provider.ValidateTokenRequest(context)
            };
        }

        private static string CreateIdToken(OpenIdConnectServerOptions options, ClaimsIdentity identity, AuthenticationProperties authProperties, string clientId, string accessToken = null, string authorizationCode = null, string nonce = null)
        {
            var inputClaims = identity.Claims;
            var outputClaims = options.ServerClaimsMapper(inputClaims).ToList();

            var hashGenerator = new OpenIdConnectHashGenerator();
            
            if (!string.IsNullOrEmpty(authorizationCode))
            {
                var cHash = hashGenerator.GenerateHash(authorizationCode, options.SigningCredentials.DigestAlgorithm);
                outputClaims.Add(new Claim(JwtRegisteredClaimNames.CHash, cHash));
            }

            if (!string.IsNullOrEmpty(accessToken))
            {
                var atHash = hashGenerator.GenerateHash(accessToken, options.SigningCredentials.DigestAlgorithm);
                outputClaims.Add(new Claim("at_hash", atHash));
            }

            if (!string.IsNullOrEmpty(nonce))
            {
                outputClaims.Add(new Claim(JwtRegisteredClaimNames.Nonce, nonce));
            }

            var iat = EpochTime.GetIntDate(options.SystemClock.UtcNow.UtcDateTime).ToString();
            outputClaims.Add(new Claim("iat", iat));

            DateTimeOffset notBefore = options.SystemClock.UtcNow;
            DateTimeOffset expires = notBefore.Add(options.IdTokenExpireTimeSpan);

            string notBeforeString;
            if (authProperties.Dictionary.TryGetValue("IdTokenIssuedUtc", out notBeforeString))
            {
                DateTimeOffset value;
                if (DateTimeOffset.TryParseExact(notBeforeString, "r", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
                    notBefore = value;
            }

            string expiresString;
            if (authProperties.Dictionary.TryGetValue("IdTokenExpiresUtc", out expiresString))
            {
                DateTimeOffset value;
                if (DateTimeOffset.TryParseExact(expiresString, "r", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
                    expires = value;
            }

            var jwt = options.TokenHandler.CreateToken(
                issuer: options.IssuerName,
                signingCredentials: options.SigningCredentials,
                audience: clientId,
                notBefore: notBefore.UtcDateTime,
                expires: expires.UtcDateTime,
                signatureProvider: options.SignatureProvider
            );

            jwt.Payload.AddClaims(outputClaims);

            var idToken = options.TokenHandler.WriteToken(jwt);

            return idToken;
        }
      
    }
}
