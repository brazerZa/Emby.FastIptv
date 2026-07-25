define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-radio'], function (BaseView, loading) {
    'use strict';

    var pluginId = 'C7D8E9F0-A1B2-4C3D-8E4F-5A6B7C8D9E0F';

    // Two mutually exclusive modes, stored in the single AdvertiseStreamMetadata boolean.
    function getMode(view) {
        return view.querySelector('#radioFixedCodecs').checked ? 'fixed' : 'fastprobe';
    }

    function updateVisibility(view) {
        var mode = getMode(view);
        view.querySelector('#fastProbeOptions').style.display = mode === 'fastprobe' ? '' : 'none';
        view.querySelector('#fixedCodecOptions').style.display = mode === 'fixed' ? '' : 'none';
    }

    function loadPage(view, config) {
        view.querySelector('#txtVideoCodec').value = config.DefaultVideoCodec || 'h264';
        view.querySelector('#txtAudioCodec').value = config.DefaultAudioCodec || 'aac';
        view.querySelector('#txtContainer').value = config.DefaultContainer || 'ts';
        view.querySelector('#radioFixedCodecs').checked = config.AdvertiseStreamMetadata === true;
        view.querySelector('#radioFastProbe').checked = config.AdvertiseStreamMetadata !== true;
        view.querySelector('#txtProbeTimeoutSeconds').value = config.ProbeTimeoutSeconds || 4;
        view.querySelector('#txtProbeMaxKilobytes').value = config.ProbeMaxKilobytes || 4096;
        view.querySelector('#txtProbeCacheHours').value = config.ProbeCacheHours || 24;
        view.querySelector('#txtUserAgent').value = config.UserAgent || '';
        view.querySelector('#txtCacheTtlHours').value = config.CacheTtlHours || 6;
        view.querySelector('#txtStreamTimeoutSeconds').value = config.StreamTimeoutSeconds || 15;
        view.querySelector('#txtStreamRetryCount').value = config.StreamRetryCount != null ? config.StreamRetryCount : 2;
        updateVisibility(view);
        loading.hide();
    }

    function onSubmit(e) {
        e.preventDefault();
        loading.show();
        var form = this;
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.DefaultVideoCodec = form.querySelector('#txtVideoCodec').value || 'h264';
            config.DefaultAudioCodec = form.querySelector('#txtAudioCodec').value || 'aac';
            config.DefaultContainer = form.querySelector('#txtContainer').value || 'ts';
            config.AdvertiseStreamMetadata = getMode(form) === 'fixed';
            config.ProbeTimeoutSeconds = parseInt(form.querySelector('#txtProbeTimeoutSeconds').value, 10) || 4;
            config.ProbeMaxKilobytes = parseInt(form.querySelector('#txtProbeMaxKilobytes').value, 10) || 4096;
            config.ProbeCacheHours = parseInt(form.querySelector('#txtProbeCacheHours').value, 10) || 24;
            config.UserAgent = form.querySelector('#txtUserAgent').value;
            config.CacheTtlHours = parseInt(form.querySelector('#txtCacheTtlHours').value, 10) || 6;
            config.StreamTimeoutSeconds = parseInt(form.querySelector('#txtStreamTimeoutSeconds').value, 10) || 15;
            config.StreamRetryCount = parseInt(form.querySelector('#txtStreamRetryCount').value, 10) || 0;
            return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then(function () {
            Dashboard.processPluginConfigurationUpdateResult();
        });
        return false;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        view.querySelector('form').addEventListener('submit', onSubmit);

        var modeRadios = view.querySelectorAll('input[name="streamMetadataMode"]');
        for (var i = 0; i < modeRadios.length; i++) {
            modeRadios[i].addEventListener('change', function () {
                updateVisibility(view);
            });
        }
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loading.show();
        var page = this.view;
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            loadPage(page, config);
        });
    };

    return View;
});
