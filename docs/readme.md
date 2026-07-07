# ศูนย์รวมเอกสารระบบ CostFlow (CostFlow Documentation Hub)

โฟลเดอร์ `docs/` จัดเก็บเอกสารทางเทคนิค คู่มือสถาปัตยกรรมระบบ และอธิบายตรรกะการทำงานของแต่ละฟีเจอร์ สำหรับนักพัฒนาและทีมงานที่เกี่ยวข้อง

---

## สารบัญเอกสาร (Table of Contents)

### 📘 โครงสร้างโปรเจกต์และสถาปัตยกรรม (Architecture & Project Structure)
*   **[00_project_structure_and_architecture.md](00_project_structure_and_architecture.md)** — อธิบายโครงสร้างโฟลเดอร์ โค้ดแต่ละส่วน ระบบ MVC สถาปัตยกรรมระบบ และโฟลว์การทำงานหลักแบบละเอียดกระชับ

### 📗 คู่มือตรรกะการทำงานแต่ละโมดูล (Module Logic Guides)
*   **[01_file_merge.md](01_file_merge.md)** — ระบบอัปโหลดและเปรียบเทียบไฟล์แผนผลิตกับใบขออนุมัติสั่งผลิต (File Merge & Price Matching)
*   **[02_export_excel.md](02_export_excel.md)** — ระบบการออกรายงานและดาวน์โหลดไฟล์สรุปผล Excel (ClosedXML Export)
*   **[03_purchase_order.md](03_purchase_order.md)** — ระบบค้นหาอะไหล่อัตโนมัติ (AJAX) และบันทึกใบสั่งซื้อสะสม
*   **[04_price_reference.md](04_price_reference.md)** — ระบบจัดการตารางราคากลางอะไหล่อ้างอิงและประวัติราคา (Price Reference CRUD)
*   **[05_reports.md](05_reports.md)** — ระบบรายงานสรุปผลต่างราคา ประวัติการทำงาน และการเชื่อมต่อ Apache Kafka