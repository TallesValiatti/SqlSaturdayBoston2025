using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Identity;
using OverviewAzureAiAgentService.Api.Services.Models;
using Thread = OverviewAzureAiAgentService.Api.Services.Models.Thread;

namespace OverviewAzureAiAgentService.Api.Services;

public class AgentService(IConfiguration configuration)
{
    private readonly string _annotationMark = "📖";
    
    private PersistentAgentsClient CreateAgentsClient()
    {
        var connectionString = configuration["AiServiceProjectConnectionString"]!;
        return new PersistentAgentsClient(connectionString, new DefaultAzureCredential());
    }
    
    private AIProjectClient CreateProjectClient()
    {
        var connectionString = configuration["AiServiceProjectConnectionString"]!;
        return new AIProjectClient(new Uri(connectionString), new DefaultAzureCredential());
    }

    public async Task<Agent> CreateAgentAsync(CreateAgentRequest request)
    {
        var aiModel = configuration["AiModel"]!;
        var index = configuration["AzureAiSearchIndex"]!;
        
        var agentClient = CreateAgentsClient();
        var projectClient = CreateProjectClient();

        var connectionsClient = projectClient.GetConnectionsClient();
        var connections = connectionsClient.GetConnectionsAsync();

        var azureAiSearchConnectionId = "";
        await foreach (var connection in connections)
        {
            if (connection.Type != ConnectionType.AzureAISearch) continue;
            azureAiSearchConnectionId = connection.Id;
            break;
        }
        
        AzureAISearchToolResource searchResource = new(
            indexConnectionId: azureAiSearchConnectionId,
            indexName: index,
            topK: 10,
            filter: string.Empty,
            queryType: AzureAISearchQueryType.VectorSimpleHybrid
        );
        
        ToolResources toolResource = new() { AzureAISearch = searchResource };

        var agentResponse = await agentClient.Administration.CreateAgentAsync(
            model: aiModel,
            name: request.Name,
            instructions: request.Instructions,
            tools: [new AzureAISearchToolDefinition()],
            toolResources: toolResource);
        
        return new Agent(
            agentResponse.Value.Id,
            agentResponse.Value.Name,
            agentResponse.Value.Instructions);
    }

    public async Task<Thread> CreateThreadAsync()
    {
        var client = CreateAgentsClient();

        var threadResponse = await client.Threads.CreateThreadAsync();

        return new Thread(threadResponse.Value.Id);
    }

    public async Task<Message> CreateRunAsync(CreateRunRequest request)
    {
        var client = CreateAgentsClient();

        await client.Messages.CreateMessageAsync(
            request.ThreadId,
            MessageRole.User,
            request.Message);

        Response<ThreadRun> runResponse = await client.Runs.CreateRunAsync(
            request.ThreadId,
            request.AgentId,
            additionalInstructions: "");

        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            runResponse = await client.Runs.GetRunAsync(request.ThreadId, runResponse.Value.Id);
            
        } while (runResponse.Value.Status == RunStatus.Queued || 
                 runResponse.Value.Status == RunStatus.InProgress ||
                 runResponse.Value.Status == RunStatus.RequiresAction);

        if (runResponse.Value.Status == RunStatus.Failed)
        {
            return new Message(
                Guid.NewGuid().ToString(), 
                MessageRole.User.ToString(),
                $"Error: {runResponse.Value.LastError.Message}");
        }
        
        Pageable<PersistentThreadMessage> messages = client.Messages.GetMessages(
            request.ThreadId,
            order: ListSortOrder.Descending);
        
        var message = messages.FirstOrDefault();

        if (message is null)
        {
            throw new Exception("No messages found after run.");
        }
        
        StringBuilder text = new();

        foreach (var contentItem in message.ContentItems)
        {
            if (contentItem is MessageTextContent textItem)
            {
                var annotations = textItem.Annotations;

                if (annotations.Any())
                {
                    var formattedText = textItem.Text;
                    
                    foreach (var annotation in annotations)
                    {
                        if (annotation is MessageTextFileCitationAnnotation messageTextFileCitationAnnotation)
                        {
                            formattedText = formattedText.Replace(messageTextFileCitationAnnotation.Text, $" ({_annotationMark} {messageTextFileCitationAnnotation.FileId})");
                        }
                        else if (annotation is MessageTextUriCitationAnnotation messageTextUriCitationAnnotation)
                        {
                            formattedText = formattedText.Replace(messageTextUriCitationAnnotation.Text, $" ({_annotationMark} {messageTextUriCitationAnnotation.UriCitation.Title})");
                        }
                    }
                    text.AppendLine(formattedText);
                }
                else
                {
                    text.AppendLine(textItem.Text);
                }
            }
        }

        if (message.ContentItems.Count == 1 && text.Length > 0)
        {
            text.Length--;
        }

        return new Message(message.Id, message.Role.ToString(), text.ToString());
    }

    public async Task<IEnumerable<Message>> ListMessagesAsync(string threadId)
    {
        var client = CreateAgentsClient();

        Pageable<PersistentThreadMessage> messages = client.Messages.GetMessages(threadId);

        return messages.Select(message =>
        {
            StringBuilder text = new();

            foreach (var contentItem in message.ContentItems)
            {
                if (contentItem is MessageTextContent textItem)
                {
                    text.AppendLine(textItem.Text);
                }
            }

            if (message.ContentItems.Count == 1 && text.Length > 0)
            {
                text.Length--;
            }

            return new Message(message.Id, message.Role.ToString(), text.ToString());
        });
    }

    private async Task<string> UploadFileAsync(string content, string fileName)
    {
        var client = CreateAgentsClient();
        
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var fileResponse = await client.Files.UploadFileAsync(stream, PersistentAgentFilePurpose.Agents, fileName);

        return fileResponse.Value.Id;
    }
    
    private async Task AddFilesToVectorStoreAsync(string vectorStoreId, IList<string> fileIds)
    {
        var client = CreateAgentsClient();

        foreach (var fileId in fileIds)
        {
            await client.VectorStores.CreateVectorStoreFileAsync(vectorStoreId, fileId);
        }
    }
    
    private async Task<string> CreateDocVectorStoreAsync(IList<string> files)
    {
        var client = CreateAgentsClient();

        var vectorStore = await client.VectorStores.CreateVectorStoreAsync(fileIds: files, name: "my-docs");

        await AddFilesToVectorStoreAsync(vectorStore.Value.Id, files);
        
        return vectorStore.Value.Id;
    }
}