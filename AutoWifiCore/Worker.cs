using System.Net.NetworkInformation;
using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Interactions;
using OpenQA.Selenium.Support.UI;

namespace AutoWifiCore
{
    public class Worker(ILogger<Worker> logger) : BackgroundService
    {
        private bool _connectionGoodLogged = false;
        private const string PortalUrl = "http://rescue.wi-mesh.vn/login";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wifiConnected = IsWifiConnected();

                if (wifiConnected)
                {
                    if (await IsInternetDown())
                    {
                        _connectionGoodLogged = false;
                        logger.LogWarning("WiFi connected but no internet - Trying captive portal...");
                        await AutoClickConnect();
                    }
                    else
                    {
                        if (!_connectionGoodLogged)
                        {
                            logger.LogInformation("Connection is good and usable!");
                            _connectionGoodLogged = true;
                        }
                    }
                }
                else
                {
                    _connectionGoodLogged = false;
                    logger.LogWarning("No WiFi connection detected - Please connect to WiFi network first");
                }

                // Check every five seconds
                await Task.Delay(5000, stoppingToken);
            }
        }

        private string? FindEdgeDriverPath()
        {
            // First check current directory (build output contains msedgedriver.exe)
            var currentDirDriver = Path.Combine(Directory.GetCurrentDirectory(), "msedgedriver.exe");
            if (File.Exists(currentDirDriver))
            {
                return Directory.GetCurrentDirectory();
            }

            // Then check base directory (for service running from different location)
            var baseDirDriver = Path.Combine(AppContext.BaseDirectory, "msedgedriver.exe");
            if (File.Exists(baseDirDriver))
            {
                return AppContext.BaseDirectory;
            }

            var possiblePaths = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedgedriver.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedgedriver.exe",
                @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgedriver.exe",
                @"C:\Program Files\Microsoft\EdgeWebView\Application\msedgedriver.exe",
                Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\Edge\User Data\msedgedriver.exe"),
                Environment.ExpandEnvironmentVariables(@"%PROGRAMFILES%\Microsoft\Edge\Application\msedgedriver.exe"),
                Environment.ExpandEnvironmentVariables(@"%PROGRAMFILES(X86)%\Microsoft\Edge\Application\msedgedriver.exe"),
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    var directory = Path.GetDirectoryName(path);
                    return directory;
                }
            }

            // Try to find Edge installation and check for driver in subdirectories
            var edgeInstallPaths = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%PROGRAMFILES%\Microsoft\Edge\Application"),
                Environment.ExpandEnvironmentVariables(@"%PROGRAMFILES(X86)%\Microsoft\Edge\Application"),
            };

            foreach (var basePath in edgeInstallPaths)
            {
                if (Directory.Exists(basePath))
                {
                    var versionDirs = Directory.GetDirectories(basePath);
                    foreach (var versionDir in versionDirs)
                    {
                        var driverPath = Path.Combine(versionDir, "msedgedriver.exe");
                        if (File.Exists(driverPath))
                        {
                            return versionDir;
                        }
                    }
                }
            }

            return null;
        }

        private bool IsWifiConnected()
        {
            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

                foreach (var networkInterface in networkInterfaces)
                {
                    // Check if it's a wireless interface and is up/connected
                    if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        networkInterface.OperationalStatus == OperationalStatus.Up)
                    {
                        if (!_connectionGoodLogged) 
                            logger.LogInformation($"WiFi connected: {networkInterface.Description}");
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Error checking WiFi status: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> IsInternetDown()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                // Check if we get redirected to captive portal (follow redirects)
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

                // First try - check msftconnecttest (Microsoft's test endpoint)
                var response = await client.GetAsync("http://www.msftconnecttest.com/connecttest.txt");

                if (!response.IsSuccessStatusCode)
                {
                    return true;
                }

                // Check content - if it's not the expected content, we're in captive portal
                var content = await response.Content.ReadAsStringAsync();

                // If we get redirected or get HTML (login page), it's captive portal
                if (content.Contains("Microsoft Connect Test"))
                {
                    return false; // Real internet
                }

                // Check if response is HTML (login page) instead of plain text
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.Contains("text/html"))
                {
                    return true; // Captive portal login page
                }

                // Try another endpoint as backup - Google
                var googleResponse = await client.GetAsync("http://clients1.google.com/generate_204");
                if (!googleResponse.IsSuccessStatusCode)
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        private async Task AutoClickConnect()
        {
            EdgeOptions options = new EdgeOptions();
            //run without showing the browser window
            options.AddArgument("--headless");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--no-sandbox");

            // Try to find EdgeDriver in system locations
            var edgeDriverPath = FindEdgeDriverPath();
            EdgeDriverService edgeDriverService;

            if (edgeDriverPath != null)
            {
                edgeDriverService = EdgeDriverService.CreateDefaultService(edgeDriverPath);
                logger.LogInformation($"Using EdgeDriver from: {edgeDriverPath}");
            }
            else
            {
                logger.LogWarning("EdgeDriver not found in system, trying default service...");
                edgeDriverService = EdgeDriverService.CreateDefaultService();
            }

            edgeDriverService.HideCommandPromptWindow = true;

            using IWebDriver driver = new EdgeDriver(edgeDriverService, options);
            try
            {
                logger.LogInformation("Navigating to captive portal...");
                driver.Navigate().GoToUrl(PortalUrl);

                await Task.Delay(7000);

                IWebElement? connectButton = null;
                try
                {
                    connectButton = driver.FindElement(By.Id("connectToInternet"));
                }
                catch
                {
                    connectButton = driver.FindElement(By.XPath("//*[contains(text(), 'Kết nối')] | //*[contains(text(), 'Connect')]"));
                }

                if (connectButton != null)
                {
                    ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView(true);", connectButton);

                    // Wait for button to be enabled (disabled class removed after 6s countdown)
                    logger.LogInformation("Waiting for button to be enabled...");
                    WebDriverWait buttonWait = new WebDriverWait(driver, TimeSpan.FromSeconds(8));
                    buttonWait.Until(d =>
                    {
                        try
                        {
                            var btn = d.FindElement(By.Id("connectToInternet"));
                            var classList = btn.GetAttribute("class") ?? "";
                            return !classList.Contains("disabled");
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    logger.LogInformation("Button is now enabled, trying to call underlying function...");

                    // Try to call the underlying function directly
                    try
                    {
                        ((IJavaScriptExecutor)driver).ExecuteScript("if (typeof slideBannerFunctions !== 'undefined' && typeof slideBannerFunctions.connectToWifi === 'function') slideBannerFunctions.connectToWifi();");
                        logger.LogInformation("Direct function call succeeded!");
                    }
                    catch (Exception funcEx)
                    {
                        logger.LogError($"Function call failed: {funcEx.Message}");
                    }
                }

                logger.LogInformation("Waiting for portal to finalize session...");
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                logger.LogError($"Error: {ex.Message}");
            }
            finally
            {
                driver.Quit();
            }
        }
    }
}
