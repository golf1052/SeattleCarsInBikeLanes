const isDesktop = window.screen.availWidth >= 576;
let blueskyDid = null;
let blueskyAccessJwt = null;

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
        img.style = 'max-width: 25rem;';
        img.src = metadata.uri;
        carouselItem.appendChild(img);
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
    card.id = metadata.photoId;
    card.style = 'max-width: 25rem;';

    let picture;
    if (metadatas.length === 1) {
        picture = document.createElement('img');
        picture.src = metadata.uri;
    } else {
        picture = createPictureCarousel(key, metadatas);
    }
    
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
    if (metadata.userSpecifiedDateTime) {
        dateInput.style = 'color: red;';
    }
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
    if (metadata.userSpecifiedDateTime) {
        timeInput.style = 'color: red;';
    }
    timeInput.value = dateTime.toLocaleString(luxon.DateTime.TIME_24_SIMPLE);
    timeInputDiv.appendChild(timeInput);
    timeRow.appendChild(timeInputDiv);

    const locationRow = createTextInputRow('Location:', 'location', metadata.photoCrossStreet, metadata.userSpecifiedLocation);
    const gpsRow = createTextInputRow('GPS:', 'gps', `${metadata.photoLatitude}, ${metadata.photoLongitude}`, metadata.userSpecifiedLocation);
    const twitterAttributionRow = createTextInputRow('Twitter Attribution:', 'twitterSubmittedBy', metadata.twitterSubmittedBy);
    const mastodonAttributionRow = createTextInputRow('Mastodon Attribution:', 'mastodonSubmittedBy', metadata.mastodonSubmittedBy);

    const copyButton = createElementWithClass('button', 'btn btn-light me-4');
    copyButton.innerHTML = '<i class="bi bi-clipboard"></i>';
    copyButton.addEventListener('click', function() {
        const carString = metadata.numberOfCars === 1 ? 'car' : 'cars';
        let submissionString = 'Submission';
        if (metadata.mastodonSubmittedBy !== 'Submission') {
            const splitMastodonSubmittedBy = metadata.mastodonSubmittedBy.split(' ');
            const splitUsername = splitMastodonSubmittedBy[2].split('@');
            submissionString = `Submitted by https://${splitUsername[2]}/@${splitUsername[1]}`;
        }
        const copyString =
            `${metadata.numberOfCars} ${carString}\n` +
            `Date: ${dateTime.toFormat('M/d/yyyy')}\n` +
            `Time: ${dateTime.toFormat('h:mm a')}\n` +
            `Location: ${metadata.photoCrossStreet}\n` +
            `GPS: ${metadata.photoLatitude}, ${metadata.photoLongitude}\n` +
            `${submissionString}`;
        navigator.clipboard.writeText(copyString);
    });
    const uploadButton = createSubmitButton('btn-success', 'Upload');
    uploadButton.className = 'btn btn-success me-4';
    const deleteButton = createSubmitButton('btn-danger', 'Delete');
    const buttonDiv = createElementWithClass('div', 'text-center');
    buttonDiv.append(copyButton, uploadButton, deleteButton);

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

                if (name === 'gps') {
                    const [latitude, longitude] = value.split(',');
                    if (latitude.trim() !== metadata.photoLatitude) {
                        metadata.photoLatitude = latitude.trim();
                    }
                    if (longitude.trim() !== metadata.photoLongitude) {
                        metadata.photoLongitude = longitude.trim();
                    }
                }

                if (name === 'twitterSubmittedBy') {
                    if (value.trim() !== metadata.twitterSubmittedBy) {
                        metadata.twitterSubmittedBy = value.trim();
                    }
                }

                if (name === 'mastodonSubmittedBy') {
                    if (value.trim() !== metadata.mastodonSubmittedBy) {
                        metadata.mastodonSubmittedBy = value.trim();
                    }
                }
            }

            if (!metadata.twitterSubmittedBy) {
                metadata.twitterSubmittedBy = 'Submission';
            }
            
            if (!metadata.mastodonSubmittedBy) {
                metadata.mastodonSubmittedBy = 'Submission';
            }

            uploadTweet(metadatas)
            .then(() => {
                document.getElementById(metadata.photoId).remove();
                return displayPendingPhotos();
            })
            .catch(error => {
                changeLoadingButtonToRegularButton(submitButton, 'Upload');
            });
        } else if (submitButton.innerText === 'Delete') {
            changeButtonToLoadingButton(submitButton, 'Deleting...');
            deletePendingPhoto(metadatas)
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
    postTweet('', tweetTextArea.value, tweetImagesTextArea.value, postTweetInput.value, quoteTweetInput.value, blueskyDid, blueskyAccessJwt)
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
    const cardsDiv = document.getElementById('cardsDiv');
    if (cardsDiv.childElementCount === 0) {
        return getPendingPhotos()
        .then(response => {
            if (Object.keys(response).length === 0) {
                cardsDiv.append('No pending reported items.');
            } else {
                const sortedKeys = Object.keys(response).sort((a, b) => {
                    const aDate = luxon.DateTime.fromISO(response[a][0].photoDateTime);
                    const bDate = luxon.DateTime.fromISO(response[b][0].photoDateTime);
                    return aDate.diff(bDate).milliseconds;
                });
                for (const key of sortedKeys) {
                    const card = createDesktopCard(key, response[key]);
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

getBlueskySession()
.then(response => {
    blueskyDid = response.did;
    blueskyAccessJwt = response.accessJwt;
    displayPendingPhotos();
});

