# 📚 CostFlow System Documentation Hub
> เอกสารคู่มือและสถาปัตยกรรมระบบบริหารจัดการและวิเคราะห์ต้นทุนการผลิต (CostFlow)

---

## 🧭 สารบัญเอกสาร (Documentation Directory)

เอกสารในโฟลเดอร์นี้ได้รับการปรับปรุงและจัดหมวดหมู่อย่างเป็นทางการ เพื่อให้ผู้ใช้งาน ผู้ดูแลระบบ (Admin) และทีมผู้พัฒนา (Developer) สามารถศึกษาทำความเข้าใจระบบได้อย่างครบถ้วน:

| เอกสาร | รายละเอียดและหัวข้อสำคัญ |
| :--- | :--- |
| 📖 [01. คู่มือสรุปการใช้งานทุกเมนู (01_Menu_and_Features_Guide.md)](file:///d:/ProjectIntern/CostFlow/docs/01_Menu_and_Features_Guide.md) | สรุปฟังก์ชันการทำงานทุกเมนูในระบบ (7 เมนูหลัก):<br>1. หน้าหลัก (Dashboard)<br>2. นำเข้า & รวมไฟล์ (File Merge)<br>3. คลังรายงาน (Reports)<br>4. คิดค่าใช้จ่ายประจำเดือน (Monthly Cost)<br>5. สรุปค่าใช้จ่ายประจำเดือน (Cost Summary)<br>6. ระบบจัดซื้อและติดตามอะไหล่ (Purchase & Tracking)<br>7. ตั้งค่าระบบและการสำรองข้อมูล (Settings & Backup) |
| ⚙️ [02. คู่มือระบบตรวจรับของ การผลัดยอด และการจำลองเวลา (02_Goods_Receipt_Deferral_and_Time_Engine.md)](file:///d:/ProjectIntern/CostFlow/docs/02_Goods_Receipt_Deferral_and_Time_Engine.md) | เจาะลึกกลไกเบื้องหลังของระบบ:<br>• กลไกการตรวจรับของ (Goods Receipt) & การตัดจ่ายต้นทุนจริง<br>• ระบบผลัดยอด (Manual Forwarding vs Auto-Skip 00:00 น.)<br>• การคำนวณยอดยกยอด (Carry-over Balance)<br>• สถาปัตยกรรมระบบเวลา (`IDateTimeProvider`, `MockDateStore`) & แผงควบคุม Dev Simulator<br>• การเชื่อมต่อและซิงค์ข้อมูลกับ Google Sheets แบบ Real-time |

---

## 🛠️ โครงสร้างไฟล์และสคริปต์เสริมในโฟลเดอร์นี้
* [google-apps-script-FINAL.js](file:///d:/ProjectIntern/CostFlow/docs/google-apps-script-FINAL.js) — สคริปต์ Google Apps Script (GAS) สำหรับนำไปติดตั้งใน Google Sheets เพื่อรับ Webhook การ Sync ข้อมูล
* [init_tables.sql](file:///d:/ProjectIntern/CostFlow/docs/init_tables.sql) — สคริปต์ DDL สำหรับสร้างตารางบนฐานข้อมูล MySQL / TiDB Cloud
* [reset_data.sql](file:///d:/ProjectIntern/CostFlow/docs/reset_data.sql) — สคริปต์สำหรับล้างข้อมูลทดสอบและเตรียมฐานข้อมูลให้พร้อมใช้งาน
