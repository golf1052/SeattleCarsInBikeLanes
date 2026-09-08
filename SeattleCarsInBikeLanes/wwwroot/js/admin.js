let blueskyAdminDid = null;
let blueskyAccessJwt = null;
let pendingRefresh = 0;
const adminDeviceBlocks = new AdminDeviceBlocks();

function createElementWithClass(tagName, className) {
    const element = document.createElement(tagName);
    element.className = className;
    return element;
}

function createTextInputRow(label, name, value, userSpecified) {
    const row = createElementWithClass('div', 'row');
    const labelDiv = createElementWithClass('div', 'col-auto');
    const labelElement = createElementWithClass('div', 'form-label');
    labelElement.append(label);
    labelDiv.appendChild(labelElement);
    row.appendChild(labelDiv);

    const inputDiv = createElementWithClass('div', 'col-12');
    const input = createElementWithClass('input', 'form-control form-control-sm');
    input.setAttribute('type', 'text');
    input.setAttribute('name', name);
    if (userSpecified) {
        input.style = 'color: red;';
    }
    input.value = value ?? '';
    input.setAttribute('aria-label', label);
    inputDiv.appendChild(input);
    row.appendChild(inputDiv);
    return row;
}

function createSubmitButton(buttonClass, text) {
    const button = createElementWithClass('button', `btn ${buttonClass}`);
    button.setAttribute('type', 'submit');
    button.append(text);
    return button;
}

function createPictureCarousel(key, metadatas) {
    const div = createElementWithClass('div', 'carousel slide');
    div.id = `carousel_${key}`;
    const innerDiv = createElementWithClass('div', 'carousel-inner');
    innerDiv.style = 'max-width: 25rem;';
    metadatas.forEach((metadata, index) => {
        const carouselItem = createElementWithClass('div', 'carousel-item');
        if (index === 0) {
            carouselItem.classList.add('active');
        }
        const img = createElementWithClass('img', 'd-block');
        img.style = 'max-width: 100%;';
        img.alt = `Report photo ${index + 1}`;
        if (metadata.uri) {
            img.src = metadata.uri;
            carouselItem.appendChild(img);
        } else {
            carouselItem.append('Preview unavailable. See the report warning below.');
        }
        innerDiv.appendChild(carouselItem);
    });
    div.appendChild(innerDiv);

    // Prev button
    const prevButton = createElementWithClass('button', 'carousel-control-prev');
    prevButton.setAttribute('type', 'button');
    prevButton.setAttribute('data-bs-target', `#carousel_${key}`);
    prevButton.setAttribute('data-bs-slide', 'prev');
    const prevIcon = createElementWithClass('span', 'carousel-control-prev-icon');
    prevIcon.setAttribute('aria-hidden', 'true');
    prevButton.appendChild(prevIcon);
    const prevSpan = createElementWithClass('span', 'visually-hidden');
    prevSpan.append('Previous');
    prevButton.appendChild(prevSpan);
    div.appendChild(prevButton);

    // Next button
    const nextButton = createElementWithClass('button', 'carousel-control-next');
    nextButton.setAttribute('type', 'button');
    nextButton.setAttribute('data-bs-target', `#carousel_${key}`);
    nextButton.setAttribute('data-bs-slide', 'next');
    const nextIcon = createElementWithClass('span', 'carousel-control-next-icon');
    nextIcon.setAttribute('aria-hidden', 'true');
    nextButton.appendChild(nextIcon);
    const nextSpan = createElementWithClass('span', 'visually-hidden');
    nextSpan.append('Next');
    nextButton.appendChild(nextSpan);
    div.appendChild(nextButton);

    return div;
}

function createDesktopCard(key, metadatas) {
    const metadata = metadatas[0];
    const dateTime = luxon.DateTime.fromISO(metadata.photoDateTime);

    const card = createElementWithClass('div', 'card');
    card.id = key;
    card.style = 'max-width: 25rem;';

    let picture;
    if (metadatas.length === 1) {
        picture = document.createElement('img');
        picture.alt = 'Report photo';
        if (metadata.uri) {
            picture.src = metadata.uri;
        } else {
            picture = document.createElement('p');
            picture.append('Preview unavailable. See the report warning below.');
        }
    } else {
        picture = createPictureCarousel(key, metadatas);
    }
    
    const cardBody = createElementWithClass('div', 'card-body');
    const status = createElementWithClass('p', 'text-break');
    status.append(`Report: ${metadata.reportId} · ${metadata.moderationStatus}`);
    if (metadata.deviceId) status.append(' · ', adminDeviceBlocks.createControl(metadata.deviceId));
    if (metadata.moderationOperationId) {
        status.append(` · Operation: ${metadata.moderationOperationId} · Started: ${metadata.moderationStartedAt}`);
    }
    cardBody.appendChild(status);
    for (const warning of new Set(metadatas.map(photo => photo.warning).filter(Boolean))) {
        const warningElement = createElementWithClass('p', 'alert alert-warning');
        warningElement.setAttribute('role', 'status');
        warningElement.append(warning);
        cardBody.appendChild(warningElement);
    }

    const form = document.createElement('form');
    form.id = `${key}_form`;

    const numberOfCarsRow = createElementWithClass('div', 'row');
    const numberOfCarsLabelDiv = createElementWithClass('div', 'col-auto');
    const numberOfCarsLabel = createElementWithClass('label', 'form-label');
    numberOfCarsLabel.append('Number of cars:');
    numberOfCarsLabelDiv.appendChild(numberOfCarsLabel);
    numberOfCarsRow.appendChild(numberOfCarsLabelDiv);

    const numberOfCarsInputDiv = createElementWithClass('div', 'col-12');
    const numberOfCarsInput = createElementWithClass('input', 'form-control form-control-sm');
    numberOfCarsInput.setAttribute('type', 'number');
    numberOfCarsInput.setAttribute('name', 'numberOfCars');
    numberOfCarsInput.setAttribute('min', '1');
    numberOfCarsInput.setAttribute('aria-label', 'Number of cars');
    numberOfCarsInput.required = true;
    numberOfCarsInput.value = metadata.numberOfCars;
    numberOfCarsInputDiv.appendChild(numberOfCarsInput);
    numberOfCarsRow.appendChild(numberOfCarsInputDiv);

    const dateRow = createElementWithClass('div', 'row');
    const dateLabelDiv = createElementWithClass('div', 'col-auto');
    const dateLabel = createElementWithClass('div', 'form-label');
    dateLabel.append('Date:');
    dateLabelDiv.appendChild(dateLabel);
    dateRow.append(dateLabelDiv);

    const dateInputDiv = createElementWithClass('div', 'col-12');
    const dateInput = createElementWithClass('input', 'form-control form-control-sm');
    dateInput.setAttribute('type', 'date');
    dateInput.setAttribute('name', 'date');
    if (metadata.userSpecifiedDateTime) {
        dateInput.style = 'color: red;';
    }
    dateInput.value = dateTime.toISODate() ?? '';
    dateInput.setAttribute('aria-label', 'Date');
    dateInput.required = true;
    dateInputDiv.appendChild(dateInput);
    dateRow.appendChild(dateInputDiv);

    const timeRow = createElementWithClass('div', 'row');
    const timeLabelDiv = createElementWithClass('div', 'col-auto');
    const timeLabel = createElementWithClass('label', 'form-label');
    timeLabel.append('Time:');
    timeLabelDiv.appendChild(timeLabel);
    timeRow.appendChild(timeLabelDiv);

    const timeInputDiv = createElementWithClass('div', 'col-12');
    const timeInput = createElementWithClass('input', 'form-control form-control-sm');
    timeInput.setAttribute('type', 'time');
    timeInput.setAttribute('name', 'time');
    if (metadata.userSpecifiedDateTime) {
        timeInput.style = 'color: red;';
    }
    timeInput.value = dateTime.isValid ? dateTime.toFormat('HH:mm') : '';
    timeInput.setAttribute('aria-label', 'Time');
    timeInput.required = true;
    timeInputDiv.appendChild(timeInput);
    timeRow.appendChild(timeInputDiv);

    const locationRow = createTextInputRow('Location:', 'location', metadata.photoCrossStreet, metadata.userSpecifiedLocation);
    const gpsRow = createTextInputRow('GPS:', 'gps', `${metadata.photoLatitude}, ${metadata.photoLongitude}`, metadata.userSpecifiedLocation);
    const twitterAttributionRow = createTextInputRow('Twitter Attribution:', 'twitterSubmittedBy', metadata.twitterSubmittedBy);
    const mastodonAttributionRow = createTextInputRow('Mastodon Attribution:', 'mastodonSubmittedBy', metadata.mastodonSubmittedBy);
    const blueskyAttributionRow = createTextInputRow('Bluesky Attribution:', 'blueskySubmittedBy', metadata.blueskySubmittedBy);
    const threadsAttributionRow = createTextInputRow('Threads Attribution:', 'threadsSubmittedBy', metadata.threadsSubmittedBy);
    const twitterLinkRow = createTextInputRow('Twitter Link:', 'twitterLink', metadata.twitterLink);

    function readEdits() {
        const fields = new FormData(form);
        const [latitude, longitude, extra] = fields.get('gps').split(',').map(value => value.trim());
        const date = luxon.DateTime.fromISO(`${fields.get('date')}T${fields.get('time')}`);
        const numberOfCars = Number(fields.get('numberOfCars'));
        if (!latitude || !longitude || extra !== undefined || !Number.isFinite(Number(latitude)) ||
            !Number.isFinite(Number(longitude)) || !date.isValid || !Number.isInteger(numberOfCars) || numberOfCars < 1) {
            throw new Error('Enter a positive car count, valid date/time, and GPS as latitude, longitude.');
        }
        return {
            numberOfCars,
            photoDateTime: date.toISO({ includeOffset: false }),
            photoCrossStreet: fields.get('location').trim(),
            photoLatitude: latitude,
            photoLongitude: longitude,
            twitterSubmittedBy: fields.get('twitterSubmittedBy').trim() || 'Submission',
            mastodonSubmittedBy: fields.get('mastodonSubmittedBy').trim() || 'Submission',
            blueskySubmittedBy: fields.get('blueskySubmittedBy').trim() || 'Submission',
            threadsSubmittedBy: fields.get('threadsSubmittedBy').trim() || 'Submission',
            twitterLink: fields.get('twitterLink').trim()
        };
    }

    const copyButton = createElementWithClass('button', 'btn btn-light me-4');
    copyButton.innerHTML = '<i class="bi bi-clipboard"></i>';
    copyButton.type = 'button';
    copyButton.setAttribute('aria-label', 'Copy report text');
    copyButton.addEventListener('click', async function() {
        try {
            const edits = readEdits();
            const date = luxon.DateTime.fromISO(edits.photoDateTime);
            const carString = edits.numberOfCars === 1 ? 'car' : 'cars';
            let attribution = edits.mastodonSubmittedBy !== 'Submission' ? edits.mastodonSubmittedBy : edits.blueskySubmittedBy;
            const mastodonMention = attribution.match(/^Submitted by @([^@\s]+)@([^@\s]+)$/);
            if (mastodonMention) attribution = `Submitted by https://${mastodonMention[2]}/@${mastodonMention[1]}`;
            else if (metadata.blueskyHandle && attribution === `Submitted by @${metadata.blueskyHandle}`) {
                attribution = `Submitted by https://bsky.app/profile/${metadata.blueskyHandle}`;
            }
            await navigator.clipboard.writeText(
                `${edits.numberOfCars} ${carString}\nDate: ${date.toFormat('M/d/yyyy')}\nTime: ${date.toFormat('h:mm a')}\n` +
                `Location: ${edits.photoCrossStreet}\nGPS: ${edits.photoLatitude}, ${edits.photoLongitude}\n${attribution}`);
        } catch (error) {
            displayError(`Could not copy report text. ${error.message}`);
        }
    });
    const uploadButton = createSubmitButton('btn-success', 'Upload');
    uploadButton.className = 'btn btn-success me-4';
    const deleteButton = createSubmitButton('btn-danger', 'Delete');
    deleteButton.formNoValidate = true;
    const buttonDiv = createElementWithClass('div', 'text-center');
    buttonDiv.append(copyButton, uploadButton, deleteButton);

    form.append(numberOfCarsRow, dateRow, timeRow, locationRow, gpsRow, twitterAttributionRow, mastodonAttributionRow, blueskyAttributionRow, threadsAttributionRow, twitterLinkRow, buttonDiv);

    const canModerate = metadatas.every(photo => photo.canModerate);
    uploadButton.disabled = !canModerate;
    deleteButton.disabled = !canModerate;
    if (!canModerate) {
        form.querySelectorAll('input').forEach(input => input.readOnly = true);
    }
    let inFlight = false;
    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        const submitButton = event.submitter;
        if (!canModerate || inFlight || ![uploadButton, deleteButton].includes(submitButton)) return;
        const publishing = submitButton === uploadButton;
        let refreshRequired = false;
        try {
            const request = {
                reportId: metadata.reportId,
                reportVersion: metadata.reportVersion,
                legacySubmissionId: metadata.legacySubmissionId,
                photoIds: metadatas.map(photo => photo.photoId),
                ...(publishing ? { edits: readEdits(), blueskyAdminDid, blueskyAccessJwt } : {})
            };
            inFlight = true;
            uploadButton.disabled = true;
            deleteButton.disabled = true;
            changeButtonToLoadingButton(submitButton, publishing ? 'Uploading...' : 'Deleting...');
            if (publishing) await uploadTweet(request);
            else await deletePendingPhoto(request);
            refreshRequired = true;
            await displayPendingPhotos();
        } catch (error) {
            displayError(error.message);
            if (error.retryAllowed) {
                // Keep the edited form and advance its version after the server releases the
                // failed attempt. A retry of an adopted report no longer uses the flat-data path.
                for (const photo of metadatas) {
                    photo.reportVersion = error.reportVersion;
                    photo.legacySubmissionId = null;
                }
            }
            const staleCard = error.retryAllowed && !card.isConnected;
            refreshRequired = refreshRequired || error.refreshRequired || staleCard;
            if (error.refreshRequired || staleCard) {
                try { await displayPendingPhotos(); }
                catch (refreshError) { displayError(`${error.message} Refresh also failed: ${refreshError.message}`); }
            }
        } finally {
            changeLoadingButtonToRegularButton(submitButton, publishing ? 'Upload' : 'Delete');
            // If reconciliation failed, do not re-enable a stale card.
            uploadButton.disabled = refreshRequired || !canModerate;
            deleteButton.disabled = refreshRequired || !canModerate;
            inFlight = false;
        }
    });

    cardBody.appendChild(form);
    card.append(picture, cardBody);
    return card;
}

document.getElementById('postMonthlyStatsButton').addEventListener('click', function(event) {
    changeButtonToLoadingButton(event.target, 'Posting...');
    const postMonthlyStatsInput = document.getElementById('postMonthlyStatsInput');
    const link = postMonthlyStatsInput.value;
    postMonthlyStats(link)
    .then(() => {
        postMonthlyStatsInput.value = '';
        changeLoadingButtonToRegularButton(event.target, 'Post');
    })
    .catch(error => {
        displayError(error.message);
        changeLoadingButtonToRegularButton(event.target, 'Post');
    });
});

document.getElementById('postLinkButton').addEventListener('click', function(event) {
    changeButtonToLoadingButton(event.target, 'Posting...');
    const postLinkInput = document.getElementById('postLinkInput');
    const link = postLinkInput.value;
    postTweet(link)
    .then(() => {
        postLinkInput.value = '';
        changeLoadingButtonToRegularButton(event.target, 'Post');
    })
    .catch(error => {
        displayError(error.message);
        changeLoadingButtonToRegularButton(event.target, 'Post');
    });
});

document.getElementById('postTweetButton').addEventListener('click', function(event) {
    changeButtonToLoadingButton(event.target, 'Posting...');
    const tweetTextArea = document.getElementById('tweetTextArea');
    const tweetImagesTextArea = document.getElementById('tweetImagesTextArea');
    const postTweetInput = document.getElementById('postTweetInput');
    const quoteTweetInput = document.getElementById('quoteTweetInput');
    postTweet('', tweetTextArea.value, tweetImagesTextArea.value, postTweetInput.value, quoteTweetInput.value, blueskyAdminDid, blueskyAccessJwt)
    .then(() => {
        tweetTextArea.value = '';
        tweetImagesTextArea.value = '';
        postTweetInput.value = '';
        quoteTweetInput.value = '';
        changeLoadingButtonToRegularButton(event.target, 'Post tweet');
    })
    .catch(error => {
        displayError(error.message);
        changeLoadingButtonToRegularButton(event.target, 'Post tweet');
    });
});

document.getElementById('deletePostButton').addEventListener('click', function(event) {
    changeButtonToLoadingButton(event.target, 'Deleting...');
    const deletePostInput = document.getElementById('deletePostInput');
    const identifier = deletePostInput.value;
    deletePost(identifier)
    .then(() => {
        deletePostInput.value = '';
        changeLoadingButtonToRegularButton(event.target, 'Delete');
    })
    .catch(error => {
        displayError(error.message);
        changeLoadingButtonToRegularButton(event.target, 'Delete');
    });
});

function displayPendingPhotos() {
    const refresh = ++pendingRefresh;
    const cardsDiv = document.getElementById('cardsDiv');
    return getPendingPhotos()
        .then(response => {
            if (refresh !== pendingRefresh) return;
            const cards = document.createDocumentFragment();
            document.getElementById('pendingItems').innerText = `Pending reports: ${Object.keys(response).length}`;
            if (Object.keys(response).length === 0) {
                cards.append('No pending reported items.');
            } else {
                const sortedKeys = Object.keys(response).sort((a, b) => {
                    const aDate = luxon.DateTime.fromISO(response[a][0].photoDateTime);
                    const bDate = luxon.DateTime.fromISO(response[b][0].photoDateTime);
                    return aDate.diff(bDate).milliseconds;
                });
                for (const key of sortedKeys) {
                    const card = createDesktopCard(key, response[key]);
                    cards.append(card);
                }
            }
            cardsDiv.replaceChildren(cards);
        });
}

document.getElementById('refreshPendingButton').addEventListener('click', () => {
    displayPendingPhotos().catch(error => displayError(error.message));
});
adminDeviceBlocks.refresh();
displayPendingPhotos().catch(error => displayError(error.message));
getBlueskySession()
.then(response => {
    blueskyAdminDid = response.did;
    blueskyAccessJwt = response.accessJwt;
})
.catch(error => displayError(`Could not load the Bluesky admin session. ${error.message}`));
