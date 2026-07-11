define(['baseView', 'loading', 'emby-input', 'emby-button'], function (BaseView, loading) {
    'use strict';

    var TUNER_TYPE = 'FastIptv';
    var PLUGIN_ID = 'C7D8E9F0-A1B2-4C3D-8E4F-5A6B7C8D9E0F';

    function View(view, params) {
        BaseView.apply(this, arguments);
        this.tunerHostId = params ? (params.id || params.Id || params.tunerHostId || null) : null;
        view.querySelector('#tunerSetupForm').addEventListener('submit', onSubmit.bind(this));
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loading.show();
        var self = this;

        if (!self.tunerHostId) {
            loading.hide();
            return;
        }

        // Load tuner definition — use getJSON so the response is always parsed JSON.
        var tunersPromise = ApiClient.getJSON(ApiClient.getUrl('LiveTv/TunerHosts'))
            .then(function (hosts) {
                var list = Array.isArray(hosts) ? hosts : [];
                var tuner = list.filter(function (t) { return t.Id === self.tunerHostId; })[0];
                if (tuner) {
                    self.view.querySelector('#txtFriendlyName').value = tuner.FriendlyName || '';
                    self.view.querySelector('#txtTunerUrl').value = tuner.Url || '';
                    self.view.querySelector('#txtTunerCount').value = tuner.TunerCount || '';
                }
            })
            .catch(function () { /* leave tuner fields blank on error */ });

        // Load per-tuner plugin settings independently so a config failure
        // does not also blank out the tuner name / URL fields.
        var configPromise = ApiClient.getPluginConfiguration(PLUGIN_ID)
            .then(function (config) {
                var tunerSettings = findTunerSettings(config || {}, self.tunerHostId);
                self.view.querySelector('#txtUserAgent').value = tunerSettings.UserAgent || '';
                self.view.querySelector('#txtCustomHeaders').value = tunerSettings.CustomHeaders || '';
                self.view.querySelector('#txtCacheTtl').value = tunerSettings.CacheTtlHours || '';
                var epgEl = self.view.querySelector('#txtEpgUrl');
                if (epgEl) epgEl.value = tunerSettings.EpgUrl || '';
                self.view.querySelector('#txtStreamTimeout').value = tunerSettings.StreamTimeoutSeconds || '';
                self.view.querySelector('#txtStreamRetryCount').value = tunerSettings.StreamRetryCount || '';
                self.view.querySelector('#chkHealthProbe').checked = tunerSettings.EnableHealthProbe || false;
            })
            .catch(function () { /* leave per-tuner fields blank on error */ });

        // Hide the loader once both are settled (each already swallows its own errors).
        Promise.all([tunersPromise, configPromise]).then(function () {
            loading.hide();
        });
    };

    function findTunerSettings(config, tunerId) {
        var entries = config.TunerSettings || [];
        return entries.filter(function (s) { return s.TunerId === tunerId; })[0] || {};
    }

    function onSubmit(e) {
        e.preventDefault();
        loading.show();
        var self = this;
        var view = self.view;

        var tunerCount = parseInt(view.querySelector('#txtTunerCount').value, 10) || 0;
        var tunerInfo = {
            Type: TUNER_TYPE,
            FriendlyName: view.querySelector('#txtFriendlyName').value || 'Fast IPTV M3U',
            Url: view.querySelector('#txtTunerUrl').value,
            TunerCount: tunerCount,
            ImportGuideData: true,
            IsEnabled: true
        };

        if (self.tunerHostId) {
            tunerInfo.Id = self.tunerHostId;
        }

        var userAgent = view.querySelector('#txtUserAgent').value || '';
        var customHeaders = view.querySelector('#txtCustomHeaders').value || '';
        var cacheTtl = parseInt(view.querySelector('#txtCacheTtl').value, 10) || 0;
        var epgUrl = view.querySelector('#txtEpgUrl').value || '';
        var streamTimeout = parseInt(view.querySelector('#txtStreamTimeout').value, 10) || 0;
        var streamRetryCount = parseInt(view.querySelector('#txtStreamRetryCount').value, 10) || 0;
        var enableHealthProbe = view.querySelector('#chkHealthProbe').checked || false;

        // Save tuner first so we have its Id, then persist the per-tuner settings
        ApiClient.ajax({
            url: ApiClient.getUrl('LiveTv/TunerHosts'),
            type: 'POST',
            data: JSON.stringify(tunerInfo),
            contentType: 'application/json'
        }).then(function (savedTuner) {
            var tunerId = (savedTuner && savedTuner.Id) ? savedTuner.Id : self.tunerHostId;

            // Nothing to save if we have no tuner id or all fields are default
            if (!tunerId || (!userAgent && !customHeaders && cacheTtl === 0 && !epgUrl
                    && streamTimeout === 0 && streamRetryCount === 0 && !enableHealthProbe)) {
                return null;
            }

            return ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
                var entries = config.TunerSettings || [];
                var idx = entries.findIndex(function (s) { return s.TunerId === tunerId; });
                var entry = {
                    TunerId: tunerId,
                    UserAgent: userAgent,
                    CustomHeaders: customHeaders,
                    CacheTtlHours: cacheTtl,
                    EpgUrl: epgUrl,
                    StreamTimeoutSeconds: streamTimeout,
                    StreamRetryCount: streamRetryCount,
                    EnableHealthProbe: enableHealthProbe
                };

                if (idx >= 0) {
                    entries[idx] = entry;
                } else {
                    entries.push(entry);
                }
                config.TunerSettings = entries;
                return ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
            });
        }).then(function () {
            loading.hide();
            history.back();
        }).catch(function () {
            loading.hide();
        });

        return false;
    }

    return View;
});
