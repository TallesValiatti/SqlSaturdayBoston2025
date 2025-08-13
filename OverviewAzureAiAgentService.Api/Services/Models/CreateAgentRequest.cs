namespace OverviewAzureAiAgentService.Api.Services.Models;

public record CreateAgentRequest(
    string Name, 
    string Instructions,
    bool IsDocAgent = false,
    bool IsSalesAgent = false,
    bool IsEmailSenderAgent = false,
    bool IsHistoryAgent = false);