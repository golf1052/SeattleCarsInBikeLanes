let map = null;
let popup = null;

function initMap() {
    map = new atlas.Map('map', {
        center: [-122.333301, 47.606501],
        zoom: 11,
        language: 'en-US',
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
        popup = new atlas.Popup();
        getAllReportedItems()
        .then(reportedItems => {
            const dataSource = createDataSource(reportedItems);
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
                        const position = new atlas.data.Position(properties.location.longitude, properties.location.latitude);
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
            })
            map.layers.add(symbols);
        });
    });

    map.events.add('zoom', function() {
        // console.log(map.getCamera().zoom);
    });
}

initMap();
