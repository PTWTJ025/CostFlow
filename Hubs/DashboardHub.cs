using Microsoft.AspNetCore.SignalR;

namespace CostFlow.Hubs
{
    public class DashboardHub : Hub
    {
        // Hub หลักสำหรับส่งข้อมูล Real-time ของ Dashboard
        public async Task BroadcastDashboardUpdate()
        {
            await Clients.All.SendAsync("ReceiveDashboardUpdate");
        }
    }
}
