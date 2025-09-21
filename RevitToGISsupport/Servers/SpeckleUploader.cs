using RevitToGISsupport.Models;
using Speckle.Core.Api;
using Speckle.Core.Api.GraphQL.Models;
using Speckle.Core.Credentials;
using Speckle.Core.Models;
using Speckle.Core.Transports;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RevitToGISsupport.Services
{
    public static class SpeckleUploader
    {
        public static async Task SendStream(GISStream stream, string serverUrl, string token)
        {
            try
            {
                // 1. Auth
                var account = new Account
                {
                    token = token,
                    serverInfo = new ServerInfo { url = serverUrl }
                };

                var client = new Client(account);

                // 2. Tạo stream mới
                var streamInput = new StreamCreateInput
                {
                    name = "Revit Export " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    description = "Dữ liệu export từ Revit",
                    isPublic = false
                };

                string streamId = await client.StreamCreate(streamInput);

                // 3. Tạo Speckle object
                var speckleObj = new Base();
                speckleObj["@data"] = stream.objects;

                // 4. Upload object
                var transport = new ServerTransport(account, streamId);
                var objectId = await Operations.Send(speckleObj, new List<ITransport> { transport });

                // 5. Commit dữ liệu
                var commitId = await client.CommitCreate(new CommitCreateInput
                {
                    streamId = streamId,
                    objectId = objectId,
                    branchName = "main",
                    message = "Revit Export"
                });

                System.Windows.MessageBox.Show(
                    $"✅ Upload thành công lên Speckle!\nStream ID: {streamId}\nCommit ID: {commitId}",
                    "Speckle Upload",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"❌ Lỗi khi upload Speckle: {ex.Message}",
                    "Speckle Upload",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
