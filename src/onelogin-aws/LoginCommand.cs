using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using IniParser;
using OneLoginApi.Exceptions;
using OneLoginApi.Models;
using OneLoginAws.Console;
using OneLoginAws.Extensions;
using OneLoginAws.Models;
using OneLoginAws.Services;
using IConsole = OneLoginAws.Console.IConsole;

namespace OneLoginAws
{
    public class LoginCommand
    {
        public static Command Create()
        {
            var command = new Command("login")
            {
                new Option(
                    new string[] { "-C", "-c", "--config-name"}
                )
                {
                    Argument = new Argument<string>()
                    {
                        Name = "configName",
                        Arity = ArgumentArity.ExactlyOne,
                    }.ValidateConfigNames(),
                    Description = " Switch configuration name within config file",
                },
                new Option(
                    new string[] { "-p", "--profile"}
                )
                {
                    Argument = new Argument<string>()
                    {
                        Name = "profile",
                    },
                    Description = "AWS profile name.",
                },
                new Option(
                    new string[] { "-u", "--username" }
                )
                {
                    Argument = new Argument<string>()
                    {
                        Name = "username",
                    },
                    Description = "AWS profile to use.",
                },
                new Option(
                    new string[] { "-r", "--region"}
                )
                {
                    Argument = new Argument<string>()
                    {
                       Name = "region",
                       Arity = ArgumentArity.ExactlyOne,
                    },
                    Description = "Specify default region for AWS profile being updated"
                },
                new Option(
                    new string[] { "-v", "--verbose"}
                )
                {
                    Argument = new Argument<bool>(),
                    Description = "Verbose output.",
                }
            };

            command.Handler = CommandHandler.Create<string, string, string, string>((profile, configName, region, username) =>
            {
                var client = new OneLoginClientFactory();
                var fileSystem = new FileSystem();
                var builder = new OptionsBuilder(fileSystem);
                var handler = new LoginCommand(client, new SystemConsole(), builder);

                return handler.InvokeAsync(profile, username, configName, region);
            });

            return command;
        }

        private const string AWS_CONFIG_FILE = ".aws/credentials";
        private static readonly Encoding _utf8WithoutBom = new UTF8Encoding(false);

        private readonly IOneLoginClientFactory _oneLoginClientFactory;
        private readonly IConsole _console;
        private readonly OptionsBuilder _appOptionsBuilder;
        private readonly AnsiStringBuilder _ansiBuilder;

        private readonly string _info;
        private readonly string _success;
        private readonly string _warning;
        private readonly string _error;
        private readonly string _question;
        private readonly string _pointer;

        public LoginCommand(IOneLoginClientFactory oneLoginClientFactory, IConsole console, OptionsBuilder appOptionsBuilder)
        {
            _oneLoginClientFactory = oneLoginClientFactory;
            _console = console;
            _appOptionsBuilder = appOptionsBuilder;

            _ansiBuilder = new AnsiStringBuilder();
            _info = _ansiBuilder.Clear().Blue("ℹ").ToString();
            _success = _ansiBuilder.Clear().Green("✔").ToString();
            _warning = _ansiBuilder.Clear().Yellow("⚠").ToString();
            _error = _ansiBuilder.Clear().Red("✖").ToString();
            _question = _ansiBuilder.Clear().Green("?").ToString();
            _pointer = _ansiBuilder.Clear().Write("\x1b[38;5;245m").Write("❯").ResetColor().ToString();
        }

        private List<IAMRole> GetIAMRoleArns(string saml)
        {
            var roles = new List<IAMRole>();
            var document = new XmlDocument();
            document.LoadXml(saml);

            var m = new XmlNamespaceManager(document.NameTable);
            m.AddNamespace("samlp", "urn:oasis:names:tc:SAML:2.0:protocol");
            m.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");

            var nodes = document.SelectNodes("/samlp:Response/saml:Assertion/saml:AttributeStatement/saml:Attribute[@Name='https://aws.amazon.com/SAML/Attributes/Role']/saml:AttributeValue", m);

            if (nodes == null) return roles;

            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];

                if (node != null)
                {
                    var parts = node.InnerText.Trim().Split(',');
                    var role = new IAMRole(
                        Role: parts[0],
                        Principal: parts[1]
                    );
                    roles.Add(role);
                }
            }

            return roles;
        }

        private bool TryGetFallbackProfile(string role, string username, [NotNullWhen(returnValue: true)] out string profile)
        {
            profile = "";

            if (Arn.TryParse(role, out var arn))
            {
                var roleName = arn.Resource.Split('/').ElementAtOrDefault(1);
                if (roleName != null)
                {
                    profile = $"{arn.AccountId}/{roleName}/{username}";
                    return true;
                }

                return false;
            }

            return false;
        }

        public Device SelectOTPDevice(SAMLResponse saml)
        {
            if (saml.Devices.Count == 1)
            {
                return saml.Devices[0];
            }

            _console.Write("  ");
            var device = _console.Select(
                message: "Select your OTP Device:",
                items: saml.Devices,
                onRenderItem: (device, selected) => $"[{device.DeviceId}]: {device.DeviceType}",
                indent: 5
            );

            _console.WriteLine(
                _ansiBuilder.Clear().EraseLines(2)
                .Write($"  {_success} Select your OTP Device: ").Cyan(device.DeviceType)
                .ToString()
            );
            return device;
        }

        public async Task InvokeAsync(string? profile, string? username, string? configName, string? region)
        {
            string? password, otp, otpDeviceId;

            _appOptionsBuilder
                .UseDefaults()
                .UseFromEnvironment()
                .UseConfigName(configName)
                .UseUsername(username)
                .UseProfile(profile)
                .UseRegion(region);

            if (_console.IsInputRedirected)
            {
                var line = _console.In.ReadLine();
                _appOptionsBuilder.UseFromJson(line);
            }

            var appOptions = _appOptionsBuilder.Build();
            (username, password, otp, otpDeviceId) = appOptions;

            var apiRegion = appOptions.BaseUri.Split(".").ElementAt(1);
            var client = _oneLoginClientFactory.Create(appOptions.ClientId, appOptions.ClientSecret, apiRegion);

            if (string.IsNullOrEmpty(username))
            {
                username = _console.Input<string>("OneLogin Username:");
                _console.Write(_ansiBuilder.Clear().EraseLines(2).ToString());
            }

            _console.WriteLine(
                _ansiBuilder
                    .Clear()
                    .Write($"{_success} OneLogin Username: ")
                    .Cyan(username)
                    .ToString()
            );

            if (string.IsNullOrEmpty(password))
            {
                password = _console.Password("OneLogin Password:");
                _console.Write(_ansiBuilder.Clear().EraseLines(2).ToString());
            }

            _console.WriteLine(
                _ansiBuilder
                    .Clear()
                    .Write($"{_success} OneLogin Password: ")
                    .Cyan("[input is masked]")
                    .ToString()
            );

            var saml = string.Empty;
            var typedOTP = otp == null;

            try
            {
                using (var spinner = _console.RenderSpinner(true))
                {
                    _console.Write("  Requesting SAML assertion");
                    var samlResponse = await client.SAML.GenerateSamlAssertion(
                        usernameOrEmail: username,
                        password: password,
                        appId: appOptions.AwsAppId,
                        subdomain: appOptions.Subdomain
                    );

                    saml = samlResponse.Data;
                    if (samlResponse.Message != "Success")
                    {
                        if (string.IsNullOrEmpty(otp) || samlResponse.Devices?.Count > 1)
                        {
                            spinner.Stop();
                            _console.WriteLine(_ansiBuilder.Clear().CursorLeft().Write($"{_warning} Requesting SAML assertion").ToString());
                        }

                        var device = SelectOTPDevice(samlResponse);

                        if (string.IsNullOrEmpty(otp))
                        {
                            _console.Write($"  ");
                            otp = _console.Input<string>("OTP Token:");
                        }

                        var factor = await client.SAML.VerifyFactor(
                            appId: appOptions.AwsAppId,
                            deviceId: device.DeviceId,
                            stateToken: samlResponse.StateToken,
                            otpToken: otp
                        );

                        saml = factor.Data;
                    }
                }

                _console.Write(_ansiBuilder.EraseLines(typedOTP ? 3 : 1).CursorLeft().ToString());
                _console.WriteLine($"{_success} Requesting SAML assertion");
                if (typedOTP) _console.WriteLine($"  {_success} OTP Token: {otp}");
            }
            catch (ApiException)
            {
                _console.Write(_ansiBuilder.EraseLines(1).CursorLeft().ToString());
                _console.WriteLine($"{_error} Requesting SAML assertion");
                throw;
            }

            var data = Convert.FromBase64String(saml);
            var decodedString = Encoding.UTF8.GetString(data);

            var roles = GetIAMRoleArns(decodedString);
            IAMRole? iamRole = null;

            if (string.IsNullOrEmpty(appOptions.RoleARN))
            {
                iamRole = _console.Select(
                    message: "Choose a role:",
                    items: roles,
                    onRenderItem: (iam, selected) => iam.Role,
                    indent: 2
                );

                _console.WriteLine(
                    _ansiBuilder
                        .Clear()
                        .EraseLines(2)
                        .Write($"{_success} Choose a role: ")
                        .Cyan(iamRole.Role)
                        .ToString()
                );
            }
            else
            {
                iamRole = roles.Where(role => role.Role == appOptions.RoleARN).FirstOrDefault();
            }

            // TODO: this ain't great 🤔
            if (iamRole is null) throw new Exception("Invalid role provided!");

            var appProfile = appOptions.Profile;

            if (appProfile is null && !TryGetFallbackProfile(iamRole.Role, username, out appProfile))
            {
                throw new Exception($"Unknown exception: can't generate profile name for role {iamRole.Role} and username {username}.");
            }

            var expires = DateTime.UtcNow;
            using (var spinner = _console.RenderSpinner(true))
            {
                _console.Write("  Saving credentials");

                var stsClient = new AmazonSecurityTokenServiceClient(new AnonymousAWSCredentials());

                var assumeRoleReq = new AssumeRoleWithSAMLRequest
                {
                    DurationSeconds = int.Parse(appOptions.DurationSeconds),
                    RoleArn = iamRole.Role,
                    PrincipalArn = iamRole.Principal,
                    SAMLAssertion = saml
                };

                var assumeRoleRes = await stsClient.AssumeRoleWithSAMLAsync(assumeRoleReq);
                expires = assumeRoleRes.Credentials.Expiration;

                var parser = new FileIniDataParser();
                var awsConfig = parser.ReadFile(
                    filePath: Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AWS_CONFIG_FILE)
                );

                awsConfig[appProfile]["aws_access_key_id"] = assumeRoleRes.Credentials.AccessKeyId;
                awsConfig[appProfile]["aws_secret_access_key"] = assumeRoleRes.Credentials.SecretAccessKey;
                awsConfig[appProfile]["aws_session_token"] = assumeRoleRes.Credentials.SessionToken;

                parser.WriteFile(
                    filePath: Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AWS_CONFIG_FILE),
                    parsedData: awsConfig,
                    fileEncoding: _utf8WithoutBom
                );
            }

            _console.WriteLine($"{_success} Saving credentials:");
            _console.WriteLine($"  {_pointer} Credentials cached in '{Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AWS_CONFIG_FILE)}'");
            _console.WriteLine($"  {_pointer} Expires at {expires.ToLocalTime():yyyy-MM-dd H:mm:sszzz}");
            _console.WriteLine($"  {_pointer} Use aws cli with --profile {appProfile}");
        }
    }

    static class LoginCommandValidators
    {
        public static Argument<T> ValidateConfigNames<T>(this Argument<T> arg)
        {
            arg.AddValidator(result =>
            {
                var fileSystem = new FileSystem();
                var value = result.GetValueOrDefault<string>();
                var sections = OptionsBuilder.GetConfigNames(fileSystem);

                if (value is null)
                {
                    return $"Empty config name!";
                }

                if (sections.Contains(value))
                {
                    return null;
                }

                return $"Given config name `{value}` does not exist in your config file.";
            });

            return arg;
        }
    }
}
