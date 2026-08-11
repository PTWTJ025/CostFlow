using Microsoft.AspNetCore.SignalR;

namespace CostFlow.Hubs
{
    public class DashboardHub : Hub
    {
        // Hub หลักสำหรับส่งข้อมูล Real-time ของ Dashboard & MonthlyCost
        public async Task BroadcastDashboardUpdate()
        {
            await Clients.All.SendAsync("ReceiveDashboardUpdate");
        }

        public async Task BroadcastMonthlyCostUpdate()
        {
            await Clients.All.SendAsync("ReceiveMonthlyCostUpdate");
        }
    }
}
