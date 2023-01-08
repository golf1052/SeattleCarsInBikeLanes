const isDesktop = window.screen.availWidth >= 576;

function createElementWithClass(tagName, className) {
    const element = document.createElement(tagName);
    element.className = className;
    return element;
}

function createTextInputRow(label, name, value) {
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
    input.value = value;
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

function createDesktopCard(metadata) {
    const dateTime = luxon.DateTime.fromISO(metadata.photoDateTime);

    const card = createElementWithClass('div', 'card');
    card.id = metadata.photoId;
    card.style = 'max-width: 25rem;';

    const image = document.createElement('img');
    image.src = metadata.uri;
    
    const cardBody = createElementWithClass('div', 'card-body');

    const form = document.createElement('form');
    form.id = `${metadata.photoId}_form`;

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
    dateInput.value = dateTime.toISODate();
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
    timeInput.value = dateTime.toLocaleString(luxon.DateTime.TIME_24_SIMPLE);
    timeInputDiv.appendChild(timeInput);
    timeRow.appendChild(timeInputDiv);

    const locationRow = createTextInputRow('Location:', 'location', metadata.photoCrossStreet);
    const gpsRow = createTextInputRow('GPS:', 'gps', `${metadata.photoLatitude}, ${metadata.photoLongitude}`);
    const twitterAttributionRow = createTextInputRow('Twitter Attribution:', 'twitterSubmittedBy', metadata.twitterSubmittedBy);
    const mastodonAttributionRow = createTextInputRow('Mastodon Attribution:', 'mastodonSubmittedBy', metadata.mastodonSubmittedBy);

    const uploadButton = createSubmitButton('btn-success', 'Upload');
    uploadButton.className = 'btn btn-success me-4';
    const deleteButton = createSubmitButton('btn-danger', 'Delete');
    const buttonDiv = createElementWithClass('div', 'text-center');
    buttonDiv.append(uploadButton, deleteButton);

    form.append(numberOfCarsRow, dateRow, timeRow, locationRow, gpsRow, twitterAttributionRow, mastodonAttributionRow, buttonDiv);

    form.addEventListener('submit', (event) => {
        event.preventDefault();
        const data = new FormData(event.target);
        const submitButton = event.submitter;

        if (submitButton.innerText === 'Upload') {
            changeButtonToLoadingButton(submitButton, 'Uploading...');
            for (const [name, value] of data) {
                if (name === 'numberOfCars') {
                    const parsedNumberOfCars = parseInt(value);
                    if (!isNaN(parsedNumberOfCars) && parsedNumberOfCars !== metadata.numberOfCars) {
                        metadata.numberOfCars = parsedNumberOfCars;
                    }
                }

                if (name === 'location') {
                    if (value.trim() !== metadata.photoCrossStreet) {
                        metadata.photoCrossStreet = value.trim();
                    }
                }
            }

            uploadTweet(metadata)
            .then(() => {
                document.getElementById(metadata.photoId).remove();
                return displayPendingPhotos();
            })
            .catch(error => {
                changeLoadingButtonToRegularButton(submitButton, 'Upload');
            });
        } else if (submitButton.innerText === 'Delete') {
            changeButtonToLoadingButton(submitButton, 'Deleting...');
            deletePendingPhoto(metadata)
            .then(() => {
                document.getElementById(metadata.photoId).remove();
                return displayPendingPhotos();
            })
            .catch(error => {
                changeLoadingButtonToRegularButton(submitButton, 'Delete');
            });
        }
    });

    cardBody.appendChild(form);
    card.append(image, cardBody);
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
    const cardsDiv = document.getElementById('cardsDiv');
    if (cardsDiv.childElementCount === 0) {
        return getPendingPhotos()
        .then(response => {
            if (response.length === 0) {
                cardsDiv.append('No pending reported items.');
            } else {
                for (let i = 0; i < response.length; i++) {
                    let metadata = response[i];
                    let card = createDesktopCard(metadata);
                    cardsDiv.append(card);
                }
            }
        })
        .catch(error => {
            const cardsDiv = document.getElementById('cardsDiv');
            const alertDiv = document.createElement('div');
            alertDiv.className = 'alert alert-danger';
            alertDiv.setAttribute('role', 'alert');
            alertDiv.append(error.message);
            cardsDiv.append(alertDiv);
        });
    } else {
        return Promise.resolve();
    }
}

displayPendingPhotos();
