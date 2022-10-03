let map = null;
let dataSource = null;
let popup = null;
let legendControl = null;
let locationInputHasFocus = false;
const darkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;

function toggleLegendControl() {
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

function initControls() {
    document.getElementById('toggleFiltersButton').addEventListener('click', function() {
        toggleLegendControl();
    });
}

function initLegendHtml() {
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

        searchReportedItems(params)
        .then(reportedItems => {
            toggleLegendControl();
            dataSource.clear();
            dataSource.add(createFeatureCollection(reportedItems));
        });
    });
    form.removeAttribute('hidden');
    return form;
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
        initControls();
        popup = new atlas.Popup();
        getAllReportedItems()
        .then(reportedItems => {
            legendControl = new atlas.control.LegendControl({
                title: 'Filters',
                style: 'auto',
                showToggle: false,
                visible: false,
                legends: [{
                    type: 'html',
                    html: initLegendHtml()
                }]
            });
            map.controls.add(legendControl, { position: 'bottom-right' });
            dataSource = createDataSource(reportedItems);
            map.sources.add(dataSource);

            const clusterBubbleLayer = new atlas.layer.BubbleLayer(dataSource, null, {
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

            const symbols = new atlas.layer.SymbolLayer(dataSource, null, {
                filter: ['!', ['has', 'point_count']] // Filters out clustered points from this layer
            });

            map.layers.add([
                clusterBubbleLayer,
                new atlas.layer.SymbolLayer(dataSource, null, {
                    iconOptions: {
                        image: 'none'
                    },
                    textOptions: {
                        textField: ['get', 'point_count_abbreviated'],
                        offset: [0, 0.4]
                    }
                }),
                symbols
            ]);

            map.events.add('click', clusterBubbleLayer, function(e) {
                if (e && e.shapes && e.shapes.length > 0 && e.shapes[0].properties.cluster) {
                    const cluster = e.shapes[0];
                    dataSource.getClusterExpansionZoom(cluster.properties.cluster_id)
                    .then(zoom => {
                        map.setCamera({
                            center: cluster.geometry.coordinates,
                            zoom: zoom,
                            type: 'ease',
                            duration: 200
                        });
                    });
                }
            });
            
            map.events.add('click', symbols, function(e) {
                if (e && e.shapes && e.shapes.length > 0) {
                    if (e.shapes[0].getProperties().cluster) {

                    } else {
                        const properties = e.shapes[0].getProperties();
                        const position = atlas.data.Position.fromLatLng(properties.location.position);
                        popup.setOptions({
                            position: position
                        });
                        popup.open(map);
                        const tweetId = properties.tweetId.split('.')[0];
                        getTwitterOEmbed(tweetId)
                        .then(html => {
                            if (html) {
                                popup.setOptions({
                                    content: html
                                });
                                twttr.ready(twttr => {
                                    twttr.widgets.load();
                                });
                            }
                        });
                    }
                }
            });

            map.layers.add(symbols);

            map.events.add('click', function (e) {
                if (e && legendControl && legendControl.getOptions().visible && locationInputHasFocus) {
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
initMap();
