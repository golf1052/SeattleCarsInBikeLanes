class AdminDeviceBlocks {
    constructor() {
        this.records = new Map();
        this.status = 'unknown';
        this.refreshVersion = 0;
        this.busy = false;
        this.action = null;
        this.list = document.getElementById('blockedDevicesList');
        this.listStatus = document.getElementById('blockedDevicesStatus');
        this.listError = document.getElementById('blockedDevicesError');
        this.refreshButton = document.getElementById('refreshBlockedDevicesButton');
        this.modalElement = document.getElementById('deviceBlockModal');
        this.modal = new bootstrap.Modal(this.modalElement);
        this.reason = document.getElementById('deviceBlockReason');
        this.savedReason = document.getElementById('deviceBlockSavedReason');
        this.modalError = document.getElementById('deviceBlockModalError');
        this.confirmButton = document.getElementById('deviceBlockModalConfirm');
        this.cancelButton = document.getElementById('deviceBlockModalCancel');
        this.modalRefresh = document.getElementById('deviceBlockModalRefresh');

        this.refreshButton.addEventListener('click', () => this.refresh());
        this.confirmButton.addEventListener('click', () => this.confirm());
        this.cancelButton.addEventListener('click', () => this.modal.hide());
        this.modalRefresh.addEventListener('click', () => this.recover());
        this.reason.addEventListener('input', () => this.reason.removeAttribute('aria-invalid'));
        this.modalElement.addEventListener('shown.bs.modal', () => {
            (this.action?.blocking ? this.reason : this.cancelButton).focus();
        });
        this.modalElement.addEventListener('hide.bs.modal', event => {
            if (this.busy) event.preventDefault();
        });
        this.modalElement.addEventListener('hidden.bs.modal', () => {
            const opener = this.action?.opener;
            this.action = null;
            this.reason.value = '';
            (opener?.isConnected && !opener.disabled ? opener : this.refreshButton).focus();
        });
    }

    createControl(deviceId) {
        const control = document.createElement('span');
        control.append(`Device: ${deviceId}`);
        if (typeof deviceId !== 'string' || !deviceId.trim()) return control;
        control.dataset.deviceBlockControl = deviceId.trim();
        const status = document.createElement('span');
        status.className = 'ms-2';
        status.setAttribute('role', 'status');
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'btn btn-outline-danger btn-sm ms-2';
        button.textContent = 'Block device';
        button.addEventListener('click', () => this.open(control.dataset.deviceBlockControl, true, button));
        control.append(status, button);
        this.updateControl(control);
        return control;
    }

    updateControl(control) {
        const deviceId = control.dataset.deviceBlockControl;
        const blocked = this.records.has(deviceId);
        control.querySelector('span').textContent = this.status === 'known' ?
            (blocked ? 'Blocked' : 'Not blocked') :
            (this.status === 'loading' ? 'Checking block status...' : 'Block status unknown');
        control.querySelector('button').disabled = this.busy || this.status !== 'known' || blocked ||
            !/^[A-Za-z0-9_-]{1,128}$/.test(deviceId);
    }

    render() {
        document.querySelectorAll('[data-device-block-control]').forEach(control => this.updateControl(control));
        const rows = document.createDocumentFragment();
        for (const [deviceId, reason] of this.records) {
            const row = document.createElement('li');
            row.className = 'list-group-item';
            const id = document.createElement('strong');
            id.className = 'text-break';
            id.textContent = deviceId;
            const text = document.createElement('p');
            text.className = 'text-break mb-2';
            text.style.whiteSpace = 'pre-wrap';
            text.textContent = reason;
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'btn btn-outline-danger btn-sm';
            button.textContent = 'Unblock';
            button.setAttribute('aria-label', `Unblock device ${deviceId}`);
            button.disabled = this.busy || this.status !== 'known';
            button.addEventListener('click', () => this.open(deviceId, false, button));
            row.append(id, text, button);
            rows.append(row);
        }
        this.list.replaceChildren(rows);
        this.listStatus.textContent = this.status === 'known' ?
            (this.records.size ? `${this.records.size} blocked device(s).` : 'No blocked devices.') :
            this.status === 'loading' ? 'Loading blocked devices...' :
                'Block status is unknown. Refresh blocked devices to try again. Any entries below are last known values.';
        this.list.setAttribute('aria-busy', String(this.status === 'loading'));
        this.refreshButton.disabled = this.busy || this.status === 'loading';
    }

    async refresh(reconciling = false) {
        if (this.busy && !reconciling) return false;
        const version = ++this.refreshVersion;
        this.status = 'loading';
        this.listError.hidden = true;
        this.render();
        try {
            const records = await getBlockedDevices();
            if (version !== this.refreshVersion) return false;
            if (!Array.isArray(records) || records.some(record =>
                !record || typeof record.deviceId !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(record.deviceId) ||
                typeof record.reason !== 'string' || !record.reason.trim() || record.reason.trim().length > 1000) ||
                new Set(records.map(record => record.deviceId)).size !== records.length) {
                throw new Error('The site returned an incomplete or unreadable blocklist. Refresh blocked devices before retrying.');
            }
            this.records = new Map(records.map(record => [record.deviceId, record.reason]));
            this.status = 'known';
            return true;
        } catch (error) {
            if (version !== this.refreshVersion) return false;
            this.status = 'unknown';
            this.listError.textContent = `Could not load blocked devices. ${error.message} Report moderation is still available.`;
            this.listError.hidden = false;
            return false;
        } finally {
            if (version === this.refreshVersion) {
                this.render();
                this.updateModal();
            }
        }
    }

    open(deviceId, blocking, opener) {
        if (this.busy || this.action || this.status !== 'known' ||
            !/^[A-Za-z0-9_-]{1,128}$/.test(deviceId) || this.records.has(deviceId) === blocking) return;
        this.action = { deviceId, blocking, opener, requiresRefresh: false, error: '', failureMessage: '' };
        document.getElementById('deviceBlockModalTitle').textContent = blocking ? 'Block device' : 'Unblock device';
        document.getElementById('deviceBlockModalTarget').textContent = deviceId;
        document.getElementById('deviceBlockModalDescription').textContent = blocking ?
            'Block future submissions from this device? Accepted reports will not be changed.' :
            'Allow future submissions from this device? Unblocking removes its saved reason. Accepted reports will not be changed.';
        document.getElementById('deviceBlockReasonGroup').hidden = !blocking;
        document.getElementById('deviceBlockSavedReasonGroup').hidden = blocking;
        this.reason.required = blocking;
        this.reason.value = '';
        this.reason.removeAttribute('aria-invalid');
        this.savedReason.textContent = this.records.get(deviceId) || '';
        this.updateModal();
        this.modal.show();
    }

    updateModal() {
        if (!this.action) return;
        const { deviceId, blocking, error, requiresRefresh } = this.action;
        const reached = this.status === 'known' && this.records.has(deviceId) === blocking;
        this.confirmButton.textContent = blocking ? 'Confirm block' : 'Confirm unblock';
        this.confirmButton.disabled = this.busy || requiresRefresh || this.status !== 'known' || reached;
        this.cancelButton.disabled = this.busy;
        this.reason.readOnly = this.busy;
        this.modalRefresh.hidden = !requiresRefresh;
        this.modalRefresh.disabled = this.busy;
        this.modalError.textContent = error;
        this.modalError.hidden = !error;
        this.modalElement.setAttribute('aria-busy', String(this.busy));
        if (this.busy) changeButtonToLoadingButton(this.confirmButton, 'Checking / saving...');
    }

    async confirm() {
        const action = this.action;
        if (!action || this.busy || action.requiresRefresh || this.status !== 'known' ||
            this.records.has(action.deviceId) === action.blocking) return;
        const reason = this.reason.value.trim();
        if (action.blocking && (!reason || reason.length > 1000)) {
            action.error = 'Enter a reason of 1-1,000 characters after trimming.';
            this.reason.setAttribute('aria-invalid', 'true');
            this.updateModal();
            this.reason.focus();
            return;
        }
        this.busy = true;
        // A pre-write list response must never undo an acknowledged write.
        ++this.refreshVersion;
        action.error = '';
        this.render();
        this.updateModal();
        let succeeded = false;
        try {
            if (action.blocking) await blockDevice(action.deviceId, reason);
            else await unblockDevice(action.deviceId);
            if (action.blocking) this.records.set(action.deviceId, reason);
            else this.records.delete(action.deviceId);
            succeeded = true;
        } catch (error) {
            action.failureMessage = error.message;
            action.error = `${action.failureMessage} The ${action.blocking ? 'block' : 'unblock'} request was not confirmed.`;
            if (error.refreshRequired) {
                action.requiresRefresh = true;
                await this.reconcile();
            }
        } finally {
            this.busy = false;
            this.render();
            this.updateModal();
            if (succeeded) this.modal.hide();
        }
    }

    async reconcile() {
        const action = this.action;
        const refreshed = await this.refresh(true);
        action.requiresRefresh = !refreshed;
        if (!refreshed) {
            action.error = `${action.failureMessage} The request was not confirmed, and block status could not be checked. Refresh status before retrying.`;
        } else {
            const blocked = this.records.has(action.deviceId);
            action.error = `${action.failureMessage} The request was not confirmed. The refreshed list shows this device as ${blocked ? 'blocked' : 'not blocked'}. ` +
                (blocked === action.blocking ? 'Close this dialog to review the current list.' : 'You can retry the request.');
            if (!action.blocking) this.savedReason.textContent = this.records.get(action.deviceId) || '';
        }
    }

    async recover() {
        if (this.busy || !this.action?.requiresRefresh) return;
        this.busy = true;
        this.updateModal();
        try {
            await this.reconcile();
        } finally {
            this.busy = false;
            this.render();
            this.updateModal();
        }
    }
}
