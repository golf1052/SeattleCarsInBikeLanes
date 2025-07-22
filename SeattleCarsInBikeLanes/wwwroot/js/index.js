let map = null;
let reportedItemsPromise = null;
let dataSource = null;
let popup = null;
let filterLegendControl = null;
let uploadLegendControl = null;
let bikeLaneLegendControl = null;
let filterStatusLegendControl = null;
let locationInputHasFocus = false;
let userMustSelectLocation = false;
let selectedPosition = null;
let photoLocationDataSource = null;
let photoLocationSymbolLayer = null;

let clusterBubbleLayer = null;
let symbolLayer = null;
let clusterBubbleNumberLayer = null;
let bikeLanesPromise = null;
let pblLayer = null;
let bblLayer = null;
let blLayer = null;
let clLayer = null;
let oLayer = null;
let trailsLayer = null;
const darkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;
let loggedInMastodonFullUsername = null;
let loggedInMastodonUsername = null;

function toggleFilterLegendControl() {
    toggleLegendControl(filterLegendControl);
}

function toggleUploadLegendControl() {
    toggleLegendControl(uploadLegendControl);
}

function toggleLegendControl(legendControl) {
    if (legendControl) {
        if (legendControl.getOptions().visible) {
            setLegendControlVisibility(legendControl, false);
        } else {
            if (window.innerWidth < 576) {
                const navbarToggler = document.getElementById('navbarToggler');
                navbarToggler.click();
            }
            setLegendControlVisibility(legendControl, true);
        }
    }
}

function setLegendControlVisibility(legendControl, visible) {
    legendControl.setOptions({
        visible: visible
    });
}

function toggleBikeLanes() {
    if (bikeLanesPromise !== null) {
        bikeLanesPromise.then(() => {
            [pblLayer, bblLayer, blLayer, clLayer, oLayer, trailsLayer].forEach(layer => {
                if (layer) {
                    if (layer.getOptions().visible) {
                        layer.setOptions({
                            visible: false
                        });
                    } else {
                        layer.setOptions({
                            visible: true
                        });
                    }
                }
            });

            if (bikeLaneLegendControl) {
                if (bikeLaneLegendControl.getOptions().visible) {
                    bikeLaneLegendControl.setOptions({
                        visible: false
                    });
                } else {
                    bikeLaneLegendControl.setOptions({
                        visible: true
                    });
                }
            }
        });
    }
}

function setMastodonButtonAsLoggedIn() {
    const mastodonButton = document.getElementById('mastodonSignInButton');
    mastodonButton.setAttribute('disabled', '');
    mastodonButton.innerText = 'Logged in with Mastodon';
}

function setMastodonButtonAsLoggedOut() {
    const mastodonButton = document.getElementById('mastodonSignInButton');
    mastodonButton.removeAttribute('disabled');
    mastodonButton.innerText = 'Sign in with Mastodon';
}

function clearMastodonAuth() {
    localStorage.removeItem('mastodonEndpoint');
    localStorage.removeItem('mastodonAccessToken');
    loggedInMastodonFullUsername = null;
    loggedInMastodonUsername = null;
    setMastodonButtonAsLoggedOut();
    document.getElementById('mastodonLogoutButton').className = 'dropdown-item disabled';
}

function checkMastodonAuth() {
    if (localStorage.getItem('mastodonAccessToken')) {
        document.getElementById('mastodonLogoutButton').className = 'dropdown-item';
        setMastodonButtonAsLoggedIn();
        getMastodonUsername()
        .then(response => {
            loggedInMastodonFullUsername = response.fullUsername;
            loggedInMastodonUsername = response.username;
        });
    } else {
        document.getElementById('mastodonLogoutButton').className = 'dropdown-item disabled';
    }
}

function loginWithMastodon() {
    const serverInput = document.getElementById('mastodonServerInput');
    let server = serverInput.value.toLowerCase();
    if (!server.startsWith('https://')) {
        server = `https://${server}`;
    }

    const mastodonNextButton = document.getElementById('mastodonNextButton');
    changeButtonToLoadingButton(mastodonNextButton, 'Login');
    getMastodonOAuthUrl(server)
    .then(response => {
        localStorage.setItem('mastodonEndpoint', server);
        window.location.href = response;
    })
    .catch(error => {
        const alertDiv = document.getElementById('modalAlertDiv');
        alertDiv.innerHTML = '';
        alertDiv.append(createAlertBanner(error.message));
        changeLoadingButtonToRegularButton(mastodonNextButton, 'Login');
    });
}

function setupPhotoLocationObjects() {
    if (photoLocationDataSource === null) {
        photoLocationDataSource = new atlas.source.DataSource(null, {
            cluster: false
        });
        map.sources.add(photoLocationDataSource);
        photoLocationSymbolLayer = new atlas.layer.SymbolLayer(photoLocationDataSource, null, {
            iconOptions: {
                image: 'marker-red'
            }
        });
        map.layers.add(photoLocationSymbolLayer);
    }
}

function showUploadForm2Error(message) {
    const alertDiv = document.getElementById('uploadForm2AlertDiv');
    alertDiv.innerText = message;
    alertDiv.removeAttribute('hidden');
}

function hideUploadForm2Error() {
    document.getElementById('uploadForm2AlertDiv').setAttribute('hidden', '');
}

function initControls() {
    document.getElementById('mastodonLogoutButton').addEventListener('click', function() {
        clearMastodonAuth();
    });

    const mastodonNextButton = document.getElementById('mastodonNextButton');

    mastodonNextButton.addEventListener('click', function() {
        loginWithMastodon();
    });

    mastodonNextButton.addEventListener('keydown', function(event) {
        if (event.key === 'Enter') {
            loginWithMastodon();
        }
    });
}

function initMapControls() {
    if (isiOS()) {
        document.getElementById('iOSNoteDiv').removeAttribute('hidden');
    }
    
    document.getElementById('toggleUploadButton').addEventListener('click', function() {
        toggleUploadLegendControl();
    });

    document.getElementById('toggleFiltersButton').addEventListener('click', function() {
        toggleFilterLegendControl();
    });

    document.getElementById('toggleBikeLanes').addEventListener('click', function() {
        toggleBikeLanes();
    });

    document.getElementById('attributeCheckbox').addEventListener('change', function(event) {
        if (event.target.checked) {
            if (localStorage.getItem('mastodonAccessToken')) {
                document.getElementById('mastodonSubmittedByInput').value = `Submitted by ${loggedInMastodonFullUsername}`;
            }
            if (window.blueskyHandle) {
                document.getElementById('blueskySubmittedByInput').value = `Submitted by ${window.blueskyHandle}`;
            }
        } else {
            document.getElementById('twitterSubmittedByInput').value = 'Submission';
            document.getElementById('mastodonSubmittedByInput').value = 'Submission';
            document.getElementById('blueskySubmittedByInput').value = 'Submission';
        }
    });
}

function getFilterStatusSummary(params) {
    const summary = [];
    if (params.get('minDate')) {
        if (!params.get('maxDate')) {
            const now = luxon.DateTime.now();
            const start = luxon.DateTime.fromISO(params.get('minDate'));
            const yearsDiff = now.diff(start, 'years').toObject().years;
            const monthsDiff = now.diff(start, 'months').toObject().months;
            const weeksDiff = now.diff(start, 'weeks').toObject().weeks;
            if (yearsDiff >= 1) {
                const roundYearsDiff = Math.round(yearsDiff);
                const yearStr = roundYearsDiff === 1 ? 'year' : 'years';
                summary.push(`Viewing results from the last ${roundYearsDiff} ${yearStr}`);
            } else if (monthsDiff >= 1) {
                const roundMonthsDiff = Math.round(monthsDiff);
                const monthStr = roundMonthsDiff === 1 ? 'month' : 'months';
                summary.push(`Viewing results from the last ${roundMonthsDiff} ${monthStr}`);
            } else {
                const roundWeeksDiff = Math.round(weeksDiff);
                const weekStr = roundWeeksDiff === 1 ? 'week' : 'weeks';
                summary.push(`Viewing results from the last ${roundWeeksDiff} ${weekStr}`);
            }
        }
    }

    return summary.join();
}

function initFilterLegendHtml() {
    document.getElementById('minDateInput').addEventListener('focus', function (event) {
        const checkedItem = document.querySelector('input[name="dateRadios"]:checked');
        if (checkedItem) {
            checkedItem.checked = false;
        }
    });
    document.getElementById('maxDateInput').addEventListener('focus', function (event) {
        const checkedItem = document.querySelector('input[name="dateRadios"]:checked');
        if (checkedItem) {
            checkedItem.checked = false;
        }
    });
    document.getElementById('minTimeInput').addEventListener('focus', function (event) {
        const checkedItem = document.querySelector('input[name="timeRadios"]:checked');
        if (checkedItem) {
            checkedItem.checked = false;
        }
    });
    document.getElementById('maxTimeInput').addEventListener('focus', function (event) {
        const checkedItem = document.querySelector('input[name="timeRadios"]:checked');
        if (checkedItem) {
            checkedItem.checked = false;
        }
    });
    document.getElementById('locationInput').addEventListener('focus', function (event) {
        locationInputHasFocus = true;
    });
    document.getElementById('locationInput').addEventListener('blur', function (event) {
        setTimeout(function () {
            locationInputHasFocus = false;
        }, 250);
    });
    const form = document.getElementById('filterForm');
    form.addEventListener('submit', function(event) {
        event.preventDefault();
        const data = new FormData(event.target);
        const params = new URLSearchParams();
        for (const [name, value] of data) {
            if (name === 'minCars' && value) {
                params.set(name, value);
            }
            if (name === 'maxCars' && value) {
                params.set(name, value);
            }
            if (name === 'dateRadios') {
                const now = luxon.DateTime.now();
                let from = null;
                if (value === 'all') {
                    continue;
                } else if (value === 'week') {
                    from = now.minus({ weeks: 1 });
                } else if (value === 'month') {
                    from = now.minus({ months: 1 });
                } else if (value === 'year') {
                    from = now.minus({ years: 1 });
                }
                params.set('minDate', from.toISODate());
            }
            if (name === 'minDate' && value) {
                params.set(name, value);
            }
            if (name === 'maxDate' && value) {
                params.set(name, value);
            }
            if (name === 'timeRadios') {
                let to = null;
                let from = null;
                if (value === 'all') {
                    continue;
                } else if (value === 'dawn') {
                    from = luxon.DateTime.now().startOf('year').set({ hour: 3 });
                    to = luxon.DateTime.now().startOf('year').set({ hour: 6 });
                } else if (value === 'morning') {
                    from = luxon.DateTime.now().startOf('year').set({ hour: 6 });
                    to = luxon.DateTime.now().startOf('year').set({ hour: 12 });
                } else if (value === 'afternoon') {
                    from = luxon.DateTime.now().startOf('year').set({ hour: 12 });
                    to = luxon.DateTime.now().startOf('year').set({ hour: 18 });
                } else if (value === 'dusk') {
                    from = luxon.DateTime.now().startOf('year').set({ hour: 18 });
                    to = luxon.DateTime.now().startOf('year').set({ hour: 21 });
                } else if (value === 'night') {
                    from = luxon.DateTime.now().startOf('year').set({ hour: 21 });
                    to = luxon.DateTime.now().startOf('year').set({ hour: 0 });
                } else if (value === 'advanceddarkness') {
                    from = luxon.DateTime.now().startOf('year').set({ hour: 0 });
                    to = luxon.DateTime.now().startOf('year').set({ hour: 3 });
                }
                params.set('minTime', from.toFormat('HH:mm'));
                params.set('maxTime', to.toFormat('HH:mm'));
            }
            if (name === 'minTime' && value) {
                params.set(name, value);
            }
            if (name === 'maxTime' && value) {
                params.set(name, value);
            }
            if (name === 'location' && value) {
                params.set(name, value);
            }
            if (name === 'distanceFromLocationInMiles' && value) {
                params.set(name, value);
            }
        }

        // Update filter status legend
        if (filterStatusLegendControl) {
            const filterStatusSummary = getFilterStatusSummary(params);
            if (!filterStatusSummary || filterStatusSummary === '') {
                setLegendControlVisibility(filterStatusLegendControl, false);
            } else {
                setLegendControlVisibility(filterStatusLegendControl, true);
            }
            filterStatusLegendControl.setOptions({
                legends: [{
                    type: 'html',
                    html: `<div style="min-width:150px;">${filterStatusSummary}</div>`
                }]
            });
        }

        reportedItemsPromise = searchReportedItems(params);
        reportedItemsPromise
        .then(reportedItems => {
            toggleFilterLegendControl();
            dataSource.clear();
            dataSource.add(createReportedItemFeatureCollection(reportedItems));
        });
    });
    form.removeAttribute('hidden');
    return form;
}

function initUpload1LegendHtml() {
    document.getElementById('photoFileInput').value = '';
    const form = document.getElementById('uploadForm1');
    const upload1Event = function(event) {
        event.preventDefault();
        const button = event.submitter;
        changeButtonToLoadingButton(button, 'Processing...');
        document.getElementById('uploadForm1AlertDiv').innerHTML = '';
        const files = Array.from(new FormData(event.target).entries());
        if (files.length === 0 || files[0][1].size === 0) {
            document.getElementById('uploadForm1AlertDiv').append('Must select a picture before uploading.');
            changeLoadingButtonToRegularButton(button, 'Process');
        } else if (files.length > 4) {
            document.getElementById('uploadForm1AlertDiv').append('A maximum of 4 images can be uploaded.');
            changeLoadingButtonToRegularButton(button, 'Process');
        } else {
            const onlyFiles = [];
            for (const file of files) {
                onlyFiles.push(file[1]);
            }
            uploadImage(onlyFiles)
            .then(response => {
                form.removeEventListener('submit', upload1Event);
                form.setAttribute('hidden', '');
                const body = document.getElementsByTagName('body')[0];
                body.appendChild(form);
                changeLoadingButtonToRegularButton(button, 'Process');

                uploadLegendControl.setOptions({
                    legends: [{
                        type: 'html',
                        html: initUpload2LegendHtml(response)
                    }]
                });
            })
            .catch(error => {
                document.getElementById('uploadForm1AlertDiv')
                    .appendChild(createAlertBanner(error.message));
                
                changeLoadingButtonToRegularButton(button, 'Process');
            });
        }
    };
    form.addEventListener('submit', upload1Event);
    form.removeAttribute('hidden');
    return form;
}

function initUpload2LegendHtml(metadatas) {
    if (metadatas.length === 1) {
        document.getElementById('uploadCarousel').setAttribute('hidden', '');
        document.getElementById('photo').src = metadatas[0].uri;
    } else {
        const carouselInner = document.getElementById('carouselInner');
        metadatas.forEach((metadata, index) => {
            const carouselItem = document.createElement('div');
            carouselItem.classList.add('carousel-item');
            if (index === 0) {
                carouselItem.classList.add('active');
            }
            const img = document.createElement('img');
            img.classList.add('d-block');
            img.src = metadata.uri;
            img.width = 300;
            carouselItem.appendChild(img);
            carouselInner.appendChild(carouselItem);
        });
    }
    
    const metadata = metadatas[0];
    document.getElementById('photoNumberOfCarsInput').value = '';

    let dateTime = null;
    if (metadata.photoDateTime) {
        dateTime = luxon.DateTime.fromISO(metadata.photoDateTime);
    }
    if (dateTime != null) {
        document.getElementById('photoDateInput').value = dateTime.toISODate();
        document.getElementById('photoTimeInput').value = dateTime.toLocaleString(luxon.DateTime.TIME_24_SIMPLE);
        for (const d of metadatas) {
            d.userSpecifiedDateTime = false;
        }
    } else {
        for (const d of metadatas) {
            d.userSpecifiedDateTime = true;
        }
        document.getElementById('photoDateInput').removeAttribute('readonly');
        document.getElementById('photoTimeInput').removeAttribute('readonly');
    }
    
    if (metadata.photoCrossStreet) {
        document.getElementById('photoLocationInput').value = metadata.photoCrossStreet;
    }
    
    if (metadata.photoLatitude && metadata.photoLongitude) {
        document.getElementById('photoGPSInput').value = `${metadata.photoLatitude}, ${metadata.photoLongitude}`;
        for (const d of metadatas) {
            d.userSpecifiedLocation = false;
        }
    } else {
        document.getElementById('photoGPSInput').value = '';
        for (const d of metadatas) {
            d.userSpecifiedLocation = true;
        }
        userMustSelectLocation = true;
        document.getElementById('selectLocationNoteDiv').removeAttribute('hidden');
        document.getElementById('locationRow').setAttribute('hidden', '');
    }
    
    document.getElementById('twitterSubmittedByInput').value = '';
    document.getElementById('mastodonSubmittedByInput').value = '';
    document.getElementById('blueskySubmittedByInput').value = '';
    document.getElementById('attributeDiv').setAttribute('hidden', '');
    document.getElementById('attributeCheckbox').checked = false;
    if ((localStorage.getItem('mastodonAccessToken') && loggedInMastodonFullUsername) || window.blueskyHandle) {
        if (localStorage.getItem('mastodonAccessToken') && loggedInMastodonFullUsername) {
            document.getElementById('mastodonSubmittedByInput').value = 'Submission';
            document.getElementById('attributeDiv').removeAttribute('hidden');
        }

        if (window.blueskyHandle) {
            document.getElementById('blueskySubmittedByInput').value = 'Submission';
            document.getElementById('attributeDiv').removeAttribute('hidden');
        }
    } else {
        const attributeDiv = document.getElementById('attributeDiv');
        if (!attributeDiv.hasAttribute('hidden')) {
            attributeDiv.setAttribute('hidden', '');
        }
        document.getElementById('signInAttributeText').removeAttribute('hidden');
        document.getElementById('twitterSubmittedByInput').value = 'Submission';
        document.getElementById('mastodonSubmittedByInput').value = 'Submission';
        document.getElementById('blueskySubmittedByInput').value = 'Submission';
    }

    clusterBubbleLayer.setOptions({
        visible: false
    });
    symbolLayer.setOptions({
        visible: false
    });
    clusterBubbleNumberLayer.setOptions({
        visible: false
    });

    if (metadata.photoLatitude && metadata.photoLongitude) {
        const photoLocation = new atlas.data.Position(parseFloat(metadata.photoLongitude),
            parseFloat(metadata.photoLatitude));
        const photoLocationFeatureCollection = new atlas.data.FeatureCollection([
            new atlas.data.Feature(new atlas.data.Point(photoLocation))
        ]);
        
        setupPhotoLocationObjects();
        photoLocationDataSource.clear();
        photoLocationDataSource.add(photoLocationFeatureCollection);
        map.setCamera({
            center: photoLocation,
            type: 'ease',
            duration: 500,
            zoom: 17
        });
    }

    const form = document.getElementById('uploadForm2');
    const upload2Event = function(event) {
        event.preventDefault();
        const button = event.submitter;
        changeButtonToLoadingButton(button, 'Uploading...');
        const data = new FormData(event.target);
        for (const [name, value] of data) {
            if (name === 'photoNumberOfCars') {
                const noc = parseInt(value);
                if (isNaN(noc)) {
                    showUploadForm2Error('Number of cars must be a number.');
                    changeLoadingButtonToRegularButton(button, 'Upload');
                    return;
                } else if (noc < 1) {
                    showUploadForm2Error('Number of cars must be at least 1.');
                    changeLoadingButtonToRegularButton(button, 'Upload');
                    return;
                }

                for (const d of metadatas) {
                    d.numberOfCars = noc;
                }
            }
            if (name === 'photoDate') {
                if (!value) {
                    showUploadForm2Error('Please select the date the report happened.');
                    changeLoadingButtonToRegularButton(button, 'Upload');
                    return;
                }
            }
            if (name === 'photoTime') {
                if (!value) {
                    showUploadForm2Error('Please select the time the report happened.');
                    changeLoadingButtonToRegularButton(button, 'Upload');
                    return;
                }
            }
            if (name === 'photoGPS') {
                if (!value) {
                    showUploadForm2Error('Please select the location the report happened.');
                    changeLoadingButtonToRegularButton(button, 'Upload');
                    return;
                } else if (userMustSelectLocation) {
                    for (const d of metadatas) {
                        d.photoLatitude = selectedPosition[1].toFixed(5).toString();
                        d.photoLongitude = selectedPosition[0].toFixed(5).toString();
                    }
                }
            }
            if (name === 'attributeCheck') {
                if (localStorage.getItem('mastodonAccessToken')) {
                    for (const d of metadatas) {
                        d.attribute = true;
                        d.mastodonFullUsername = loggedInMastodonFullUsername;
                        d.mastodonUsername = loggedInMastodonUsername;
                        d.mastodonEndpoint = localStorage.getItem('mastodonEndpoint');
                        d.mastodonAccessToken = localStorage.getItem('mastodonAccessToken');
                    }
                }

                if (window.blueskyHandle) {
                    for (const d of metadatas) {
                        d.attribute = true;
                        d.blueskyHandle = window.blueskyHandle;
                        d.blueskyUserDid = window.blueskyUserDid;
                        d.blueskyUserKeyId = window.blueskyAuthInfo.keyId;
                        d.blueskyUserPrivateKey = window.blueskyAuthInfo.privateKey;
                        d.blueskyUserBaseUrl = window.blueskyPds;
                        d.blueskyUserAccessToken = window.blueskyAuthInfo.accessToken;
                    }
                }
            }
            if (name === 'mastodonSubmittedBy') {
                for (const d of metadatas) {
                    d.mastodonSubmittedBy = value;
                }
            }
            if (name === 'blueskySubmittedBy') {
                for (const d of metadatas) {
                    d.blueskySubmittedBy = value;
                }
            }
        }

        if (metadata.userSpecifiedDateTime) {
            const userSpecifiedDateTime =
                luxon.DateTime.fromISO(`${document.getElementById('photoDateInput').value}T${document.getElementById('photoTimeInput').value}`);
            if (luxon.DateTime.now() <= userSpecifiedDateTime) {
                showUploadForm2Error('Selected date and time must be in the past.');
                changeLoadingButtonToRegularButton(button, 'Upload');
                return;
            }

            for (const d of metadatas) {
                d.photoDateTime = userSpecifiedDateTime.toISO();
            }
        }

        if (localStorage.getItem('mastodonAccessToken')) {
            for (const d of metadatas) {
                d.mastodonFullUsername = loggedInMastodonFullUsername;
            }
        }

        finalizeUploadImage(metadatas)
        .then(() => {
            userMustSelectLocation = false;
            selectedPosition = null;
            document.getElementById('photoNumberOfCarsInput').value = '';
            document.getElementById('photoDateInput').setAttribute('readonly', '');
            document.getElementById('photoDateInput').value = '';
            document.getElementById('photoTimeInput').setAttribute('readonly', '');
            document.getElementById('photoTimeInput').value = '';
            document.getElementById('photoLocationInput').value = '';
            document.getElementById('photoGPSInput').value = '';
            document.getElementById('selectLocationNoteDiv').setAttribute('hidden', '');
            hideUploadForm2Error();
            document.getElementById('uploadCarousel').removeAttribute('hidden');
            document.getElementById('carouselInner').innerHTML = '';
            document.getElementById('locationRow').removeAttribute('hidden');
            document.getElementById('photo').src = '';
            clusterBubbleLayer.setOptions({
                visible: true
            });
            symbolLayer.setOptions({
                visible: true
            });
            clusterBubbleNumberLayer.setOptions({
                visible: true
            });
            photoLocationDataSource.clear();
            map.setCamera({
                center: [-122.333301, 47.606501],
                type: 'ease',
                duration: 500,
                zoom: 11
            });

            form.removeEventListener('submit', upload2Event);
            form.setAttribute('hidden', '');
            const body = document.getElementsByTagName('body')[0];
            body.appendChild(form);
            changeLoadingButtonToRegularButton(button, 'Upload');

            uploadLegendControl.setOptions({
                legends: [{
                    type: 'html',
                    html: initUploadDoneLegendHtml()
                }]
            });
        })
        .catch(error => {
            showUploadForm2Error(error.message);
            changeLoadingButtonToRegularButton(button, 'Upload');
        });
    };
    form.addEventListener('submit', upload2Event);
    form.removeAttribute('hidden');
    return form;
}

function initUploadDoneLegendHtml() {
    const div = document.getElementById('uploadDoneDiv');
    div.removeAttribute('hidden');
    setTimeout(() => {
        uploadLegendControl.setOptions({
            visible: false
        });
        div.setAttribute('hidden', '');
        const body = document.getElementsByTagName('body')[0];
        body.appendChild(div);

        uploadLegendControl.setOptions({
            legends: [{
                type: 'html',
                html: initUpload1LegendHtml()
            }]
        });
    }, 3000);
    return div;
}

function initMap() {
    map = new atlas.Map('map', {
        center: [-122.333301, 47.606501],
        zoom: 11,
        language: 'en-US',
        style: darkMode ? 'night' : 'road',
        authOptions: {
            authType: 'anonymous',
            clientId: 'df857d2c-3805-4793-90e4-63e84a499756',
            getToken: function(resolve, reject, map) {
                fetch('/api/AzureMaps')
                .then(r => r.text())
                .then(token => resolve(token));
            }
        }
    });

    map.events.add('ready', function() {
        initMapControls();
        popup = new atlas.Popup();

        map.controls.add(new atlas.control.StyleControl({
            mapStyles: ['road', 'night', 'satellite_road_labels', 'grayscale_dark', 'grayscale_light', 'road_shaded_relief', 'high_contrast_dark', 'high_contrast_light']
        }), {
            position: 'top-left'
        });

        // Only initially display items reported in the last month
        const now = luxon.DateTime.now();
        const lastMonth = now.minus({ months: 1 });
        const searchParams = new URLSearchParams();
        searchParams.set('minDate', lastMonth.toISODate());
        reportedItemsPromise = searchReportedItems(searchParams);
        reportedItemsPromise
        .then(reportedItems => {
            filterLegendControl = new atlas.control.LegendControl({
                title: 'Filters',
                style: 'auto',
                showToggle: false,
                visible: false,
                legends: [{
                    type: 'html',
                    html: initFilterLegendHtml()
                }]
            });
            map.controls.add(filterLegendControl, { position: 'bottom-right' });

            uploadLegendControl = new atlas.control.LegendControl({
                title: 'Upload',
                style: 'auto',
                showToggle: false,
                visible: false,
                legends: [{
                    type: 'html',
                    html: initUpload1LegendHtml()
                }]
            });
            map.controls.add(uploadLegendControl, { position: 'bottom-right' });

            // Add filter status legend control (top-right)
            filterStatusLegendControl = new atlas.control.LegendControl({
                title: 'Current Filters',
                style: 'auto',
                showToggle: false,
                visible: true,
                legends: [{
                    type: 'html',
                    html: `<div style="min-width:150px;">${getFilterStatusSummary(searchParams)}</div>`
                }]
            });
            map.controls.add(filterStatusLegendControl, { position: 'top-right' });

            dataSource = createDataSource(reportedItems);
            map.sources.add(dataSource);

            clusterBubbleLayer = new atlas.layer.BubbleLayer(dataSource, null, {
                radius: [
                    'step',
                    ['get', 'point_count'],
                    20, // Default radius size of 20 pixels
                    5, 30, // If point_count >= 5, radius is 30 pixels
                    10, 40 // If point_count >= 10, radius is 40 pixels
                ],

                color: [
                    'step',
                    ['get', 'point_count'],
                    'rgba(0, 255, 0, 0.6)',
                    5, 'rgba(255, 255, 0, 0.6)',
                    10, 'rgba(255, 0 , 0, 0.6)'
                ],
                strokeWidth: 0,
                filter: ['has', 'point_count']
            });

            map.events.add('mouseenter', clusterBubbleLayer, () => {
                map.getCanvasContainer().style.cursor = 'pointer';
            });

            map.events.add('mouseleave', clusterBubbleLayer, () => {
                map.getCanvasContainer().style.cursor = 'grab';
            });

            symbolLayer = new atlas.layer.SymbolLayer(dataSource, null, {
                filter: ['!', ['has', 'point_count']] // Filters out clustered points from this layer
            });

            clusterBubbleNumberLayer = new atlas.layer.SymbolLayer(dataSource, null, {
                iconOptions: {
                    image: 'none'
                },
                textOptions: {
                    textField: ['get', 'point_count_abbreviated'],
                    offset: [0, 0.4]
                }
            });

            map.layers.add([
                clusterBubbleLayer,
                clusterBubbleNumberLayer,
                symbolLayer
            ]);

            bikeLanesPromise = getBikeLaneGeometry()
            .then(lanes => {
                bikeLaneLegendControl = new atlas.control.LegendControl({
                    title: 'Bike Facilities',
                    style: 'auto',
                    visible: false,
                    legends: [{
                        type: 'category',
                        itemLayout: 'row',
                        shape: 'line',
                        fitItems: true,
                        shapeSize: 20,
                        items: [
                            { label: 'Protected Bike Lane', color: 'rgb(22, 145, 208)', strokeWidth: 4 },
                            { label: 'Buffered Bike Lane', color: 'rgb(28, 179, 255)', strokeWidth: 3 },
                            { label: 'Painted Bike Lane', color: 'rgb(255, 108, 44)', strokeWidth: 3 },
                            { label: 'Climbing Lane', color: 'rgb(0, 168, 93)', strokeWidth: 2 },
                            { label: 'Miscellaneous Off Street Bicycle Facility', color: darkMode ? '#fff': '#000', strokeWidth: 2 },
                            { label: 'Multi-Use Trail', color: 'rgb(168, 56, 0)', strokeWidth: 4 }
                        ]
                    }]
                });
                map.controls.add(bikeLaneLegendControl, { position: 'bottom-left' });

                pblLayer = getLineLayer(getProtectedBikeLanesCollection(lanes), 'rgb(22, 145, 208)', 4, map);
                bblLayer = getLineLayer(getBufferedBikeLanesCollection(lanes), 'rgb(28, 179, 255)', 3, map);
                blLayer = getLineLayer(getPaintedBikeLanesCollection(lanes), 'rgb(255, 108, 44)', 3, map);
                clLayer = getLineLayer(getClimbingLanesCollection(lanes), 'rgb(0, 168, 93)', 2, map);
                otherBikeLaneColor = darkMode ? '#fff' : '#000';
                oLayer = getLineLayer(getOtherLanesCollection(lanes), otherBikeLaneColor, 2, map);
                map.layers.add(pblLayer);
                map.layers.add(bblLayer);
                map.layers.add(blLayer);
                map.layers.add(clLayer);
                map.layers.add(oLayer);
                map.layers.move(pblLayer, clusterBubbleLayer);
                map.layers.move(bblLayer, clusterBubbleLayer);
                map.layers.move(blLayer, clusterBubbleLayer);
                map.layers.move(clLayer, clusterBubbleLayer);
                map.layers.move(oLayer, clusterBubbleLayer);

                map.events.add('click', pblLayer, function(e) {
                    onBikeLaneClick(e, 'Protected Bike Lane');
                });
                map.events.add('click', bblLayer, function(e) {
                    onBikeLaneClick(e, 'Buffered Bike Lane');
                });
                map.events.add('click', blLayer, function(e) {
                    onBikeLaneClick(e, 'Painted Bike Lane');
                });
                map.events.add('click', clLayer, function(e) {
                    onBikeLaneClick(e, 'Climbing Lane');
                });
                map.events.add('click', oLayer, function(e) {
                    onBikeLaneClick(e, 'Miscellaneous Off Street Bicycle Facility');
                });

                return getTrailsGeometry();
            })
            .then(lanes => {
                trailsLayer = getLineLayer(getTrailsCollection(lanes), 'rgb(168, 56, 0)', 4, map);
                map.layers.add(trailsLayer);
                map.layers.move(trailsLayer, clusterBubbleLayer);

                map.events.add('click', trailsLayer, function(e) {
                    if (e && e.shapes && e.shapes.length > 0) {
                        let trailName = e.shapes[0].properties.ORD_STNAME_CONCAT;

                        // To title case from https://stackoverflow.com/a/196991/6681022
                        trailName = trailName.replace(/\w\S*/g, function(txt) {
                            return txt.charAt(0).toUpperCase() + txt.substr(1).toLowerCase();
                        })
                        .replace(/\btrl\b/gi, 'Trail');
                        popup.setOptions({
                            position: e.position,
                            content: `<div class="popup-content">${trailName}</div>`
                        });
                        popup.open(map);
                    }
                });
            });

            const spiderClusterManager = new atlas.SpiderClusterManager(map, clusterBubbleLayer, symbolLayer);

            map.events.add('featureSelected', spiderClusterManager, function(e) {
                showReportedItemPopup(e.shape.getProperties(), popup, map);
            });

            map.events.add('featureUnselected', spiderClusterManager, function(e) {
                spiderClusterManager.hideSpiderCluster();
            });

            map.layers.add(symbolLayer);

            map.events.add('click', function (e) {
                if (e) {
                    if (filterLegendControl && filterLegendControl.getOptions().visible && locationInputHasFocus) {
                        document.getElementById('locationInput').value = JSON.stringify(e.position);
                    } else if (uploadLegendControl && uploadLegendControl.getOptions().visible && userMustSelectLocation) {
                        document.getElementById('photoGPSInput').value = `${e.position[1].toFixed(5)}, ${e.position[0].toFixed(5)}`;
                        selectedPosition = e.position;
                        setupPhotoLocationObjects();
                        const photoLocationFeatureCollection = new atlas.data.FeatureCollection([
                            new atlas.data.Feature(new atlas.data.Point(e.position))
                        ]);
                        photoLocationDataSource.clear();
                        photoLocationDataSource.add(photoLocationFeatureCollection);
                    }
                }
            });
        });
    });

    map.events.add('zoom', function() {
        // console.log(map.getCamera().zoom);
    });
}

function initDarkMode() {
    if (!darkMode) {
        return;
    }
    const nav = document.querySelector('nav');
    nav.classList.remove('bg-light');
    nav.classList.add('navbar-dark', 'bg-dark');
}

initDarkMode();
initControls();
initMap();
checkMastodonAuth();
