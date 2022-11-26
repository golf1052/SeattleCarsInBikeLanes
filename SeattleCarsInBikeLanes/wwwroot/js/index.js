let map = null;
let reportedItemsPromise = null;
let dataSource = null;
let popup = null;
let filterLegendControl = null;
let uploadLegendControl = null;
let bikeLaneLegendControl = null;
let locationInputHasFocus = false;

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
let loggedInTwitterUsername = null;
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
            legendControl.setOptions({
                visible: false
            });
        } else {
            legendControl.setOptions({
                visible: true
            });
        }
    }
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

function setTwitterButtonAsLoggedIn() {
    const twitterButton = document.getElementById('twitterSignInButton');
    const newTwitterButton = document.createElement('button');
    newTwitterButton.className = 'btn btn-info twitter-button dropdown-item';
    newTwitterButton.id = 'twitterSignInButton';
    newTwitterButton.setAttribute('disabled', '');
    newTwitterButton.innerText = 'Logged in with Twitter';
    twitterButton.replaceWith(newTwitterButton);
}

function setTwitterButtonAsLoggedOut() {
    const twitterButton = document.getElementById('twitterSignInButton');
    const newTwitterButton = document.createElement('a');
    newTwitterButton.className = 'btn btn-info twitter-button';
    newTwitterButton.id = 'twitterSignInButton';
    newTwitterButton.href = 'https://twitter.com/i/oauth2/authorize?response_type=code&client_id=RXYtYnN5b2hsMUo3ZjlSZ2p6bEE6MTpjaQ&redirect_uri=https://seattle.carinbikelane.com/redirect&scope=tweet.read%20users.read%20offline.access&state=randomstate&code_challenge=plain&code_challenge_method=plain';
    newTwitterButton.innerText = 'Sign in with Twitter';
    twitterButton.replaceWith(newTwitterButton);
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

function clearTwitterAuth() {
    localStorage.removeItem('twitterAccessToken');
    localStorage.removeItem('twitterRefreshToken');
    localStorage.removeItem('twitterExpiresAt');
    loggedInTwitterUsername = null;
    setTwitterButtonAsLoggedOut();
    document.getElementById('twitterLogoutButton').className = 'dropdown-item disabled';
}

function clearMastodonAuth() {
    localStorage.removeItem('mastodonEndpoint');
    localStorage.removeItem('mastodonAccessToken');
    loggedInMastodonFullUsername = null;
    loggedInMastodonUsername = null;
    setMastodonButtonAsLoggedOut();
    document.getElementById('mastodonLogoutButton').className = 'dropdown-item disabled';
}

function checkTwitterAuthExpiration() {
    const tokenExpiresAt = luxon.DateTime.fromISO(localStorage.getItem('twitterExpiresAt'));
    const now = luxon.DateTime.utc();
    if (tokenExpiresAt <= now.minus({ minutes: 5 })) {
        return refreshTwitterToken(localStorage.getItem('twitterRefreshToken'))
        .catch(() => {
            clearTwitterAuth();
        });
    } else {
        return Promise.resolve();
    }
}

function checkTwitterAuth() {
    if (localStorage.getItem('twitterAccessToken')) {
        document.getElementById('twitterLogoutButton').className = 'dropdown-item';
        checkTwitterAuthExpiration()
        .then(() => {
            setTwitterButtonAsLoggedIn();
            return getTwitterUsername();
        })
        .then(response => {
            loggedInTwitterUsername = response.username;
        });
    } else {
        document.getElementById('twitterLogoutButton').className = 'dropdown-item disabled';
    }
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
    let server = serverInput.value;
    if (!server.startsWith('https://')) {
        server = `https://${server}`;
    }

    const mastodonNextButton = document.getElementById('mastodonNextButton');
    changeButtonToLoadingButton(mastodonNextButton, 'Next');
    getMastodonOAuthUrl(server)
    .then(response => {
        localStorage.setItem('mastodonEndpoint', server);
        window.location.href = response;
    })
    .catch(error => {
        const alertDiv = document.getElementById('modalAlertDiv');
        alertDiv.innerHTML = '';
        alertDiv.append(createAlertBanner(error.message));
        changeLoadingButtonToRegularButton(mastodonNextButton, 'Next');
    });
}

function initControls() {
    document.getElementById('twitterLogoutButton').addEventListener('click', function() {
        clearTwitterAuth();
    });

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
            if (localStorage.getItem('twitterAccessToken')) {
                document.getElementById('twitterSubmittedByInput').value = `Submitted by @${loggedInTwitterUsername}`;
            }
            
            if (localStorage.getItem('mastodonAccessToken')) {
                document.getElementById('mastodonSubmittedByInput').value = `Submitted by ${loggedInMastodonFullUsername}`;
            }
        } else {
            document.getElementById('twitterSubmittedByInput').value = 'Submission';
            document.getElementById('mastodonSubmittedByInput').value = 'Submission';
        }
    });
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
    form.addEventListener('submit', function(event) {
        event.preventDefault();
        const button = event.submitter;
        changeButtonToLoadingButton(button, 'Processing...');
        document.getElementById('uploadForm1AlertDiv').innerHTML = '';
        const files = Array.from(new FormData(event.target).entries());
        if (files.length === 1 && files[0][1].size > 0) {
            uploadImage(files[0][1])
            .then(response => {
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
    });
    form.removeAttribute('hidden');
    return form;
}

function initUpload2LegendHtml(metadata) {
    document.getElementById('photo').src = metadata.uri;
    const dateTime = luxon.DateTime.fromISO(metadata.photoDateTime);
    document.getElementById('photoDateInput').value = dateTime.toISODate();
    document.getElementById('photoTimeInput').value = dateTime.toLocaleString(luxon.DateTime.TIME_24_SIMPLE);
    document.getElementById('photoLocationInput').value = metadata.photoCrossStreet;
    document.getElementById('photoGPSInput').value = `${metadata.photoLatitude}, ${metadata.photoLongitude}`;
    if ((localStorage.getItem('twitterAccessToken') && loggedInTwitterUsername) || (localStorage.getItem('mastodonAccessToken') && loggedInMastodonFullUsername)) {
        if (localStorage.getItem('twitterAccessToken') && loggedInTwitterUsername) {
            document.getElementById('twitterSubmittedByInput').value = 'Submission';
            document.getElementById('attributeDiv').removeAttribute('hidden');
        }

        if (localStorage.getItem('mastodonAccessToken') && loggedInMastodonFullUsername) {
            document.getElementById('mastodonSubmittedByInput').value = 'Submission';
            document.getElementById('attributeDiv').removeAttribute('hidden');
        }
    } else {
        const attributeDiv = document.getElementById('attributeDiv');
        if (!attributeDiv.hasAttribute('hidden')) {
            attributeDiv.setAttribute('hidden', '');
        }
        document.getElementById('signInAttributeText').removeAttribute('hidden');
        document.getElementById('twitterSubmittedByInput').value = 'Submission';
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
    const photoLocation = new atlas.data.Position(parseFloat(metadata.photoLongitude),
        parseFloat(metadata.photoLatitude));
    const photoLocationFeatureCollection = new atlas.data.FeatureCollection([
        new atlas.data.Feature(new atlas.data.Point(photoLocation))
    ]);
    const photoLocationDataSource = new atlas.source.DataSource(null, {
        cluster: false
    });
    photoLocationDataSource.add(photoLocationFeatureCollection);
    map.sources.add(photoLocationDataSource);
    const photoLocationSymbolLayer = new atlas.layer.SymbolLayer(photoLocationDataSource, null, {
        iconOptions: {
            image: 'marker-red'
        }
    });
    map.layers.add(photoLocationSymbolLayer);
    map.setCamera({
        center: photoLocation,
        type: 'ease',
        duration: 500,
        zoom: 17
    });

    const form = document.getElementById('uploadForm2');
    form.addEventListener('submit', function(event) {
        event.preventDefault();
        const button = event.submitter;
        changeButtonToLoadingButton(button, 'Uploading...');
        const data = new FormData(event.target);
        for (const [name, value] of data) {
            if (name === 'photoNumberOfCars') {
                metadata.numberOfCars = parseInt(value);
            }
            if (name === 'attributeCheck') {
                if (localStorage.getItem('twitterAccessToken')) {
                    metadata.attribute = true;
                    metadata.twitterUsername = loggedInTwitterUsername;
                    metadata.twitterAccessToken = localStorage.getItem('twitterAccessToken');
                }

                if (localStorage.getItem('mastodonAccessToken')) {
                    metadata.attribute = true;
                    metadata.mastodonFullUsername = loggedInMastodonFullUsername;
                    metadata.mastodonUsername = loggedInMastodonUsername;
                    metadata.mastodonEndpoint = localStorage.getItem('mastodonEndpoint');
                    metadata.mastodonAccessToken = localStorage.getItem('mastodonAccessToken');
                }
            }
            if (name === 'twitterSubmittedBy') {
                metadata.twitterSubmittedBy = value;
            }
            if (name === 'mastodonSubmittedBy') {
                metadata.mastodonSubmittedBy = value;
            }
        }
        
        if (localStorage.getItem('twitterAccessToken')) {
            metadata.twitterUsername = loggedInTwitterUsername;
        }

        if (localStorage.getItem('mastodonAccessToken')) {
            metadata.mastodonFullUsername = loggedInMastodonFullUsername;
        }

        finalizeUploadImage(metadata)
        .then(() => {

            clusterBubbleLayer.setOptions({
                visible: true
            });
            symbolLayer.setOptions({
                visible: true
            });
            clusterBubbleNumberLayer.setOptions({
                visible: true
            });
            map.layers.remove(photoLocationSymbolLayer);
            map.sources.remove(photoLocationDataSource);
            map.setCamera({
                center: [-122.333301, 47.606501],
                type: 'ease',
                duration: 500,
                zoom: 11
            });

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
        });
    });
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
            
            map.events.add('click', symbolLayer, function(e) {
                if (e && e.shapes && e.shapes.length > 0) {
                    if (e.shapes[0].getProperties().cluster) {

                    } else {
                        showReportedItemPopup(e.shapes[0].getProperties(), popup, map);
                    }
                }
            });

            map.layers.add(symbolLayer);

            map.events.add('click', function (e) {
                if (e && filterLegendControl && filterLegendControl.getOptions().visible && locationInputHasFocus) {
                    document.getElementById('locationInput').value = JSON.stringify(e.position);
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
checkTwitterAuth();
checkMastodonAuth();
