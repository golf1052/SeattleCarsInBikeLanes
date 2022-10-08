function getBikeLaneGeometry() {
    return fetch('api/BikeLanes?type=bikelanes')
    .then(response => {
        if (!response.ok) {
            throw new Error(`Error when fetching geometry for bike lanes.`);
        }

        return response.json();
    })
    .then(response => {
        const lanes = response.filter(l => {
            return l.properties.CATEGORY !== 'BKF-SHW' && // Sharrows aren't bike lanes
                l.properties.CATEGORY !== 'BKF-NGW'; // Neighborhood greenways aren't bike lanes
        });
        return lanes;
    });
}

function getTrailsGeometry() {
    return fetch('api/BikeLanes?type=trails')
    .then(response => {
        if (!response.ok) {
            throw new Error(`Error when fetching geometry for trails.`);
        }

        return response.json();
    })
    .then(response => {
        return response;
    });
}

function getProtectedBikeLanesCollection(lanes) {
    return getLaneCollection('BKF-PBL', lanes);
}

function getBufferedBikeLanesCollection(lanes) {
    return getLaneCollection('BKF-BBL', lanes);
}

function getPaintedBikeLanesCollection(lanes) {
    return getLaneCollection('BKF-BL', lanes);
}

function getClimbingLanesCollection(lanes) {
    return getLaneCollection('BKF-CLMB', lanes);
}

function getOtherLanesCollection(lanes) {
    return getLaneCollection('BKF-OFFST', lanes);
}

function getTrailsCollection(lanes) {
    return new atlas.data.FeatureCollection(lanes);
}

function getLaneCollection(category, lanes) {
    return new atlas.data.FeatureCollection(lanes.filter(l => {
        return l.properties.CATEGORY === category;
    }));
}

function createBikeLaneDataSource(collection) {
    const source = new atlas.source.DataSource();
    source.add(collection);
    return source;
}

function getLineLayer(collection, color, width, map) {
    const dataSource = createBikeLaneDataSource(collection);
    map.sources.add(dataSource);
    const lineLayer = new atlas.layer.LineLayer(dataSource, null, {
        strokeColor: color,
        strokeWidth: width,
        visible: false
    });
    map.events.add('mouseenter', lineLayer, () => {
        map.getCanvasContainer().style.cursor = 'pointer';
    });
    map.events.add('mouseleave', lineLayer, () => {
        map.getCanvasContainer().style.cursor = 'grab';
    });
    return lineLayer;
}

function onBikeLaneClick(event, label) {
    if (event && event.shapes && event.shapes.length > 0) {
        popup.setOptions({
            position: event.position,
            content: `<div class="popup-content">${label}</div>`
        });
        popup.open(map);
    }
}
