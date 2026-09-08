// State belongs to the open upload page; closing it does not create an offline queue.
class ReportUpload {
    constructor(files) {
        this.id = crypto.randomUUID().replaceAll('-', '');
        this.files = files;
        this.prepared = null;
        this.request = null;
        this.receipt = null;
        this.pending = null;
        this.uncertain = false;
    }

    async prepare() {
        this.prepared = await uploadImage(this.files, this.id);
        return this.prepared;
    }

    submit(photos, attribution) {
        if (this.pending) {
            return this.pending;
        }
        this.pending = this.send(photos, attribution).finally(() => { this.pending = null; });
        return this.pending;
    }

    async send(photos, attribution) {
        if (this.receipt) {
            return this.receipt;
        }
        try {
            if (this.uncertain) {
                const accepted = await getUploadReceipt(this.id);
                if (accepted) {
                    return this.accept(accepted);
                }
            }

            // Freeze the submitted values while the outcome is uncertain. A retry cannot
            // silently pick up a different account or edits made after the original request.
            if (!this.request) {
                this.request = structuredClone({ photos, attribution });
            }
            this.usePreparedReferences();
            this.uncertain = true;
            try {
                return this.accept(await finalizeUploadImage(this.request.photos, this.id, this.request.attribution));
            } catch (error) {
                if (!(error instanceof UploadRequestError) || error.code !== 'preparation_expired') {
                    throw error;
                }
                // Expiry can require new photo IDs, but never a new logical report ID.
                await this.prepare();
                this.usePreparedReferences();
                return this.accept(await finalizeUploadImage(this.request.photos, this.id, this.request.attribution));
            }
        } catch (error) {
            if (error instanceof UploadRequestError && error.status >= 400 && error.status < 500 &&
                error.status !== 408 && error.status !== 429 && error.code !== 'report_in_progress') {
                this.uncertain = false;
                this.request = null;
            }
            throw error;
        }
    }

    usePreparedReferences() {
        if (!this.prepared || this.prepared.length !== this.request.photos.length) {
            throw new Error('The prepared photos do not match this report. Please start the upload again.');
        }
        this.request.photos.forEach((photo, index) => {
            photo.photoId = this.prepared[index].photoId;
            photo.submissionId = this.prepared[index].submissionId;
            photo.photoNumber = this.prepared[index].photoNumber;
        });
    }

    accept(receipt) {
        this.receipt = receipt;
        this.uncertain = false;
        this.request = null;
        this.files = [];
        return receipt;
    }
}
