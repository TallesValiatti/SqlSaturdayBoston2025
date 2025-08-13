using SendGrid;
using SendGrid.Helpers.Mail;

namespace OverviewAzureAiAgentService.Api.Services;

public class EmailService(string secret, string senderEmail)
{
    public async Task<string> SendAsync(
        string receiver,
        string subject, 
        string body)
    {
        body =  Models.Emailbody.Body.Replace("{body}", body);
        
        var client = new SendGridClient(secret);
        var from = new EmailAddress(senderEmail, senderEmail);
        
        var to = new EmailAddress(receiver, receiver);

        var msg = MailHelper.CreateSingleEmailToMultipleRecipients(
            from, 
            [to], 
            subject, 
            null, 
            body);

        var result = await client.SendEmailAsync(msg);

        return !result.IsSuccessStatusCode ? 
            $"Unable to send a email. Status: {result.StatusCode}" : 
            $"Email sent successfully to {receiver}. Status: {result.StatusCode}";
    }
}