// Weekly Plan Upload System - Multiple Files with Sheet Selection
// ระบบอัปโหลดแผนผลิตแบบหลายไฟล์พร้อมเลือก Sheet

const WeeklyPlanUploader = {
    uploadedFiles: [], // เก็บข้อมูล: [{sessionId, fileName, sheets, selectedSheet}]
    reportName: '',

    init(reportName) {
        this.reportName = reportName;
        this.setupEventListeners();
    },

    setupEventListeners() {
        const fileInput = document.getElementById('weeklyPlanFiles');
        const dropzone = document.getElementById('dropzone-area');
        const uploadBtn = document.getElementById('btn-upload-files');
        const processBtn = document.getElementById('btn-process-sheets');
        const backBtn = document.getElementById('btn-back-to-upload');

        // Dropzone click
        dropzone.addEventListener('click', () => fileInput.click());

        // File input change
        fileInput.addEventListener('change', (e) => this.handleFileSelection(e.target.files));

        // Drag & Drop
        dropzone.addEventListener('dragover', (e) => {
            e.preventDefault();
            dropzone.classList.add('border-blue-400', 'bg-blue-50/20');
        });

        dropzone.addEventListener('dragleave', () => {
            dropzone.classList.remove('border-blue-400', 'bg-blue-50/20');
        });

        dropzone.addEventListener('drop', (e) => {
            e.preventDefault();
            dropzone.classList.remove('border-blue-400', 'bg-blue-50/20');
            if (e.dataTransfer.files.length) {
                this.handleFileSelection(e.dataTransfer.files);
            }
        });

        // Upload button
        uploadBtn.addEventListener('click', () => this.uploadFiles());

        // Process button
        processBtn.addEventListener('click', () => this.processWeeklyPlan());

        // Back button
        backBtn.addEventListener('click', () => this.backToUpload());
    },

    handleFileSelection(files) {
        const filesArray = Array.from(files);
        
        if (filesArray.length === 0) {
            Swal.fire('คำแนะนำ', 'กรุณาเลือกไฟล์', 'warning');
            return;
        }

        if (filesArray.length > 5) {
            Swal.fire('คำแนะนำ', 'อัปโหลดได้สูงสุด 5 ไฟล์เท่านั้น', 'warning');
            return;
        }

        // แสดงรายชื่อไฟล์
        const filesList = document.getElementById('files-list');
        filesList.innerHTML = '';

        filesArray.forEach((file, index) => {
            const fileItem = document.createElement('div');
            fileItem.setAttribute('style', 'display: flex; align-items: center; gap: 12px; padding: 12px; background-color: #eff6ff; border: 1px solid #bfdbfe; border-radius: 6px; margin-bottom: 8px;');
            fileItem.innerHTML = `
                <svg style="width: 20px !important; height: 20px !important; color: #2563eb !important; flex-shrink: 0;" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
                    <path stroke-linecap="round" stroke-linejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
                <span class="text-sm font-medium text-blue-800 truncate" style="flex: 1; min-width: 0;">${file.name}</span>
                <span class="text-xs text-blue-600" style="flex-shrink: 0;">${(file.size / 1024 / 1024).toFixed(2)} MB</span>
            `;
            filesList.appendChild(fileItem);
        });

        document.getElementById('btn-upload-files').classList.remove('hidden');
    },

    async uploadFiles() {
        const fileInput = document.getElementById('weeklyPlanFiles');
        const files = fileInput.files;

        if (files.length === 0) {
            Swal.fire('คำแนะนำ', 'กรุณาเลือกไฟล์ก่อนอัปโหลด', 'warning');
            return;
        }

        // Show loading
        Swal.fire({
            title: 'กำลังอัปโหลดไฟล์...',
            html: 'กรุณารอสักครู่',
            allowOutsideClick: false,
            didOpen: () => {
                Swal.showLoading();
            }
        });

        try {
            const formData = new FormData();
            Array.from(files).forEach(file => {
                formData.append('files', file);
            });

            const response = await fetch('/FileMerge/UploadWeeklyPlanFiles', {
                method: 'POST',
                body: formData
            });

            const data = await response.json();

            if (data.success) {
                this.uploadedFiles = data.files;
                Swal.close();
                this.showSheetSelection();
            } else {
                Swal.fire('เกิดข้อผิดพลาด', data.error, 'error');
            }
        } catch (error) {
            console.error('Upload error:', error);
            Swal.fire('เกิดข้อผิดพลาด', 'ไม่สามารถอัปโหลดไฟล์ได้', 'error');
        }
    },

    showSheetSelection() {
        document.getElementById('upload-step').classList.add('hidden');
        document.getElementById('sheet-selection-step').classList.remove('hidden');

        const container = document.getElementById('sheet-selectors');
        container.innerHTML = '';

        this.uploadedFiles.forEach((file, index) => {
            const div = document.createElement('div');
            div.setAttribute('style', 'padding: 16px; border: 1px solid #e2e8f0; border-radius: 6px; background-color: #f8fafc; margin-bottom: 12px;');
            
            let sheetsOptions = '';
            file.sheets.forEach(sheet => {
                sheetsOptions += `<option value="${sheet}">${sheet}</option>`;
            });

            div.innerHTML = `
                <div style="display: flex; align-items: flex-start; gap: 12px;">
                    <div style="flex: 1; min-width: 0;">
                        <label class="block text-xs font-bold text-slate-500 uppercase" style="margin-bottom: 4px; display: block;">ไฟล์ ${index + 1}</label>
                        <p class="text-sm font-semibold text-slate-700 truncate" style="margin-bottom: 8px;">${file.fileName}</p>
                        <select class="sheet-selector w-full px-3 py-2 border border-slate-300 rounded text-sm" data-file-index="${index}">
                            <option value="">-- เลือก Sheet --</option>
                            ${sheetsOptions}
                        </select>
                    </div>
                </div>
            `;
            
            container.appendChild(div);
        });

        // Auto-select if only one sheet
        document.querySelectorAll('.sheet-selector').forEach(select => {
            if (select.options.length === 2) { // หนึ่งใน options คือ "-- เลือก Sheet --"
                select.selectedIndex = 1;
            }
        });
    },

    async processWeeklyPlan() {
        // Collect selected sheets
        const selectors = document.querySelectorAll('.sheet-selector');
        const filesData = [];

        let validationFailed = false;
        for (let index = 0; index < selectors.length; index++) {
            const select = selectors[index];
            const selectedSheet = select.value;
            if (!selectedSheet) {
                Swal.fire('ข้อมูลไม่ครบถ้วน', `กรุณาเลือก Sheet สำหรับไฟล์ ${this.uploadedFiles[index].fileName}`, 'warning');
                validationFailed = true;
                break;
            }

            filesData.push({
                sessionId: this.uploadedFiles[index].sessionId,
                selectedSheet: selectedSheet
            });
        }

        if (validationFailed) return;

        // Check for duplicates first before processing
        try {
            const checkRes = await fetch('/FileMerge/CheckDuplicateWeeklyPlans', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify({
                    reportName: this.reportName,
                    files: filesData
                })
            });
            
            const checkData = await checkRes.json();
            
            if (checkData.success && checkData.duplicates && checkData.duplicates.length > 0) {
                let listHtml = '<ul style="list-style-type: disc; padding-left: 20px; text-align: left; margin: 10px 0; font-size: 13px; color: #475569;">';
                checkData.duplicates.forEach(item => {
                    listHtml += `<li style="margin-bottom: 4px;">${item}</li>`;
                });
                listHtml += '</ul>';

                const confirmResult = await Swal.fire({
                    title: 'พบข้อมูลไฟล์ซ้ำในระบบ!',
                    html: `<div style="text-align: left;"><p>ระบบตรวจพบว่ามีข้อมูลของไฟล์และชีทดังต่อไปนี้บันทึกอยู่แล้ว:</p>${listHtml}<p style="margin-top: 12px; font-weight: bold; color: #e11d48; text-align: center;">คุณต้องการอัปโหลดเขียนทับ (Overwrite) ข้อมูลชุดเดิมใช่หรือไม่?</p></div>`,
                    icon: 'warning',
                    showCancelButton: true,
                    confirmButtonColor: '#2563eb',
                    cancelButtonColor: '#64748b',
                    confirmButtonText: 'ตกลง, อัปโหลดเขียนทับ',
                    cancelButtonText: 'ยกเลิก',
                    width: '550px'
                });

                if (!confirmResult.isConfirmed) {
                    return; // ยกเลิกการประมวลผล
                }
            } else if (!checkData.success) {
                Swal.fire('เกิดข้อผิดพลาด', checkData.error, 'error');
                return;
            }
        } catch (error) {
            console.error('Check duplicate error:', error);
            Swal.fire('เกิดข้อผิดพลาด', 'ไม่สามารถตรวจสอบข้อมูลซ้ำซ้อนได้', 'error');
            return;
        }

        // Show loading
        Swal.fire({
            title: 'กำลังประมวลผล...',
            html: 'กำลังจับคู่ข้อมูล กรุณารอสักครู่',
            allowOutsideClick: false,
            didOpen: () => {
                Swal.showLoading();
            }
        });

        try {
            const response = await fetch('/FileMerge/ProcessWeeklyPlan', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify({
                    reportName: this.reportName,
                    files: filesData
                })
            });

            const data = await response.json();

            if (data.success) {
                let resultHtml = '<div class="text-left">';
                resultHtml += `<p class="mb-3 text-green-600 font-bold">✅ ${data.message}</p>`;
                resultHtml += '<div class="text-sm space-y-2">';
                data.fileResults.forEach(file => {
                    resultHtml += `<p style="line-height: 1.5;">📄 ${file.fileName} (${file.sheetName})<br><span style="padding-left: 20px; color: #475569;">พบใบขออนุมัติทั้งหมด <strong>${file.totalCount || 0}</strong> รายการ (จับคู่สำเร็จ <strong>${file.matchedCount}</strong> รายการ)</span></p>`;
                });
                resultHtml += '</div>';
                resultHtml += '</div>';

                Swal.fire({
                    icon: 'success',
                    title: 'สำเร็จ!',
                    html: resultHtml,
                    confirmButtonText: 'ตกลง',
                    width: '600px'
                }).then(() => {
                    window.location.reload();
                });
            } else {
                Swal.fire('เกิดข้อผิดพลาด', data.error, 'error');
            }
        } catch (error) {
            console.error('Process error:', error);
            Swal.fire('เกิดข้อผิดพลาด', 'ไม่สามารถประมวลผลได้', 'error');
        }
    },

    backToUpload() {
        document.getElementById('sheet-selection-step').classList.add('hidden');
        document.getElementById('upload-step').classList.remove('hidden');
        document.getElementById('weeklyPlanFiles').value = '';
        document.getElementById('files-list').innerHTML = '';
        document.getElementById('btn-upload-files').classList.add('hidden');
        this.uploadedFiles = [];
    }
};
