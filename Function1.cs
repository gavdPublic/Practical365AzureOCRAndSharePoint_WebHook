using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.SharePoint.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Threading.Tasks;

namespace PracticalOCR
{

    /*  Add the following variables (with their values) to the "Application settings" of the Azure Function 
            whSiteListUrl			    https://[domain].sharepoint.com/sites/[sitecollectionname]
            whListName			        [listname]
            whListUserName			    [user]@[domain].onmicrosoft.com
            whListUserPw			    [password]
            azVisionApiServiceKey		[key of the Azure OCR service]
            azVisionApiOcrEndpoint		https://westeurope.api.cognitive.microsoft.com/vision/v1.0/ocr
    */

    public static class Function1
    {
        [FunctionName("Function1")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            log.Info("FunctionOCR trigger function processed a request.");

            string validationToken = GetValidationToken(req);
            if (validationToken != null)
            {
                log.Info($"---- Processing Registration");
                var myResponse = req.CreateResponse(HttpStatusCode.OK);
                myResponse.Content = new StringContent(validationToken);
                return myResponse;
            }

            var myContent = await req.Content.ReadAsStringAsync();
            var allNotifications = JsonConvert.DeserializeObject<ResponseModel<NotificationModel>>(myContent).Value;

            if (allNotifications.Count > 0)
            {
                log.Info($"---- Processing Notifications");
                string siteUrl = ConfigurationManager.AppSettings["whSiteListUrl"];
                foreach (var oneNotification in allNotifications)
                {
                    ClientContext SPClientContext = LoginSharePoint(siteUrl);
                    GetChanges(SPClientContext, oneNotification.Resource, log);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        public static ClientContext LoginSharePoint(string BaseUrl)
        {
            // Login using UserOnly Credentials (User Name and User PW)
            ClientContext cntReturn;

            string myUserName = ConfigurationManager.AppSettings["whListUserName"];
            string myPassword = ConfigurationManager.AppSettings["whListUserPw"];

            SecureString securePassword = new SecureString();
            foreach (char oneChar in myPassword) securePassword.AppendChar(oneChar);
            SharePointOnlineCredentials myCredentials = new SharePointOnlineCredentials(myUserName, securePassword);

            cntReturn = new ClientContext(BaseUrl);
            cntReturn.Credentials = myCredentials;

            return cntReturn;
        }

        public static string GetValidationToken(HttpRequestMessage req)
        {
            string strReturn = string.Empty;

            strReturn = req.GetQueryNameValuePairs()
                .FirstOrDefault(q => string.Compare(q.Key, "validationtoken", true) == 0)
                .Value;

            return strReturn;
        }

        static void GetChanges(ClientContext SPClientContext, string ListId, TraceWriter log)
        {
            Web spWeb = SPClientContext.Web;
            List myList = spWeb.Lists.GetByTitle(ConfigurationManager.AppSettings["whListName"]);
            SPClientContext.Load(myList);
            SPClientContext.ExecuteQuery();

            ChangeQuery myChangeQuery = GetChangeQueryNew(ListId);

            var allChanges = myList.GetChanges(myChangeQuery);
            SPClientContext.Load(allChanges);
            SPClientContext.ExecuteQuery();

            foreach (Change oneChange in allChanges)
            {
                if (oneChange is ChangeItem)
                {
                    int myItemId = (oneChange as ChangeItem).ItemId;

                    log.Info($"---- Changed ItemId : " + myItemId);
                    ListItem myItem = myList.GetItemById(myItemId);
                    Microsoft.SharePoint.Client.File myFile = myItem.File;
                    ClientResult<System.IO.Stream> myFileStream = myFile.OpenBinaryStream();
                    SPClientContext.Load(myFile);
                    SPClientContext.ExecuteQuery();

                    byte[] myFileBytes = ConvertStreamToByteArray(myFileStream);

                    TextAnalyzeOCRResult myResult = GetAzureTextAnalyzeOCR(myFileBytes).Result;
                    log.Info($"---- Text Analyze OCR Result : " + JsonConvert.SerializeObject(myResult));

                    myItem["Language"] = myResult.language;
                    string myText = string.Empty;
                    for (int oneLine = 0; oneLine < myResult.regions[0].lines.Count(); oneLine++)
                    {
                        for (int oneWord = 0; oneWord < myResult.regions[0].lines[oneLine].words.Count(); oneWord++)
                        {
                            myText += myResult.regions[0].lines[oneLine].words[oneWord].text + " ";
                        }
                    }
                    myItem["OCRText"] = myText;
                    myItem.Update();
                    SPClientContext.ExecuteQuery();
                    log.Info($"---- Text Analyze OCR added to SharePoint Item");
                }
            }
        }

        public static ChangeQuery GetChangeQueryNew(string ListId)
        {
            ChangeToken lastChangeToken = new ChangeToken();
            lastChangeToken.StringValue = string.Format("1;3;{0};{1};-1", ListId, DateTime.Now.AddMinutes(-1).ToUniversalTime().Ticks.ToString());
            ChangeToken newChangeToken = new ChangeToken();
            newChangeToken.StringValue = string.Format("1;3;{0};{1};-1", ListId, DateTime.Now.ToUniversalTime().Ticks.ToString());
            ChangeQuery myChangeQuery = new ChangeQuery(false, false);
            myChangeQuery.Item = true;  // Get only Item changes
            myChangeQuery.Add = true;   // Get only the new Items
            myChangeQuery.ChangeTokenStart = lastChangeToken;
            myChangeQuery.ChangeTokenEnd = newChangeToken;

            return myChangeQuery;
        }

        public static Byte[] ConvertStreamToByteArray(ClientResult<System.IO.Stream> myFileStream)
        {
            Byte[] bytReturn = null;

            using (System.IO.MemoryStream myFileMemoryStream = new System.IO.MemoryStream())
            {
                if (myFileStream != null)
                {
                    myFileStream.Value.CopyTo(myFileMemoryStream);
                    bytReturn = myFileMemoryStream.ToArray();
                }
            }

            return bytReturn;
        }

        public static async Task<TextAnalyzeOCRResult> GetAzureTextAnalyzeOCR(byte[] myFileBytes)
        {
            TextAnalyzeOCRResult resultReturn = new TextAnalyzeOCRResult();

            HttpClient client = new HttpClient();

            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", ConfigurationManager.AppSettings["azVisionApiServiceKey"]);

            string requestParameters = "language=unk&detectOrientation=true";

            string uri = ConfigurationManager.AppSettings["azVisionApiOcrEndpoint"] + "?" + requestParameters;
            string contentString = string.Empty;

            HttpResponseMessage response;

            using (ByteArrayContent content = new ByteArrayContent(myFileBytes))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                response = await client.PostAsync(uri, content);

                contentString = await response.Content.ReadAsStringAsync();

                resultReturn = JsonConvert.DeserializeObject<TextAnalyzeOCRResult>(contentString);
                return resultReturn;
            }
        }
    }

    public class TextAnalyzeOCRResult
    {
        public string language { get; set; }
        public float textAngle { get; set; }
        public string orientation { get; set; }
        public Region[] regions { get; set; }
    }

    public class Region
    {
        public string boundingBox { get; set; }
        public Line[] lines { get; set; }
    }

    public class Line
    {
        public string boundingBox { get; set; }
        public Word[] words { get; set; }
    }

    public class Word
    {
        public string boundingBox { get; set; }
        public string text { get; set; }
    }

    public class ResponseModel<T>
    {
        [JsonProperty(PropertyName = "value")]
        public List<T> Value { get; set; }
    }

    public class NotificationModel
    {
        [JsonProperty(PropertyName = "subscriptionId")]
        public string SubscriptionId { get; set; }

        [JsonProperty(PropertyName = "clientState")]
        public string ClientState { get; set; }

        [JsonProperty(PropertyName = "expirationDateTime")]
        public DateTime ExpirationDateTime { get; set; }

        [JsonProperty(PropertyName = "resource")]
        public string Resource { get; set; }

        [JsonProperty(PropertyName = "tenantId")]
        public string TenantId { get; set; }

        [JsonProperty(PropertyName = "siteUrl")]
        public string SiteUrl { get; set; }

        [JsonProperty(PropertyName = "webId")]
        public string WebId { get; set; }
    }

    public class SubscriptionModel
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Id { get; set; }

        [JsonProperty(PropertyName = "clientState", NullValueHandling = NullValueHandling.Ignore)]
        public string ClientState { get; set; }

        [JsonProperty(PropertyName = "expirationDateTime")]
        public DateTime ExpirationDateTime { get; set; }

        [JsonProperty(PropertyName = "notificationUrl")]
        public string NotificationUrl { get; set; }

        [JsonProperty(PropertyName = "resource", NullValueHandling = NullValueHandling.Ignore)]
        public string Resource { get; set; }
    }

}
