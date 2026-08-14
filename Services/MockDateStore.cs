using System;

namespace CostFlow.Services
{
    /// <summary>
    /// Interface สำหรับจัดเก็บและแชร์ Mock Date ระหว่าง HTTP Requests และ Background Services แบบ Singleton
    /// </summary>
    public interface IMockDateStore
    {
        DateTime? MockDate { get; set; }
        void Reset();
    }

    /// <summary>
    /// Singleton in-memory store สำหรับเก็บค่าจำลองวันที่ส่วนกลางของระบบ
    /// </summary>
    public class MockDateStore : IMockDateStore
    {
        private DateTime? _mockDate;
        private readonly object _lock = new();

        public DateTime? MockDate
        {
            get
            {
                lock (_lock)
                {
                    return _mockDate;
                }
            }
            set
            {
                lock (_lock)
                {
                    _mockDate = value;
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _mockDate = null;
            }
        }
    }
}
